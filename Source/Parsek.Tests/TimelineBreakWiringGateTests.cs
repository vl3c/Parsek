using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the timeline break (design 3.15 step 5): every load kind
    /// <see cref="StockShadowTakeBack.IsTimeTravelLoad"/> claims reaches a
    /// <c>StockShadowTakeBack.NoteLoad(</c> call in <c>ParsekScenario.OnLoad</c>. The plain
    /// rewind returns from its own branch before the refined classification, so it records its
    /// own break; every other time-travel kind (Re-Fly start, Discard Re-fly, stock revert,
    /// flight quickload) runs through the in-session branch's call on the refined kind. OnLoad
    /// is unreachable headlessly and the behavioural tests inject breaks through
    /// <c>AddBreakForTesting</c>, so an unwired call would leave them green; this file pins the
    /// WIRING. Comments are stripped and literals masked before any scan
    /// (<see cref="SourceScanText"/>).
    /// </summary>
    public class TimelineBreakWiringGateTests
    {
        private const string NoteLoad = "StockShadowTakeBack.NoteLoad(";
        private const string RewindBranch = "if (RewindContext.IsRewinding)";
        private const string RewindHandler = "HandleRewindOnLoad(node, recordings);";

        [Fact]
        public void TheClaimedKindsAreExactlyTheLoadsThatKeepTheLedgerAndLoadAnEarlierState()
        {
            var claimed = Enum.GetValues(typeof(LoadKind)).Cast<LoadKind>()
                .Where(StockShadowTakeBack.IsTimeTravelLoad).OrderBy(k => k).ToArray();
            Assert.Equal(new[]
            {
                LoadKind.PlainRewind, LoadKind.ReFlyStart, LoadKind.DiscardReFly,
                LoadKind.StockRevert, LoadKind.QuickloadFlight,
            }.OrderBy(k => k).ToArray(), claimed);

            // The rewind branch's condition is the classifier's PlainRewind input, and the
            // refined classification passes every non-in-session early kind through unchanged.
            Assert.Equal(EarlyLoadKind.PlainRewind,
                LoadReconcilePolicy.ClassifyEarly(true, true, false, false, false, false));
            Assert.Equal(LoadKind.PlainRewind,
                LoadReconcilePolicy.ClassifyRefined(EarlyLoadKind.PlainRewind, false, false, false));
            Assert.Equal(LoadKind.ReFlyStart,
                LoadReconcilePolicy.ClassifyRefined(EarlyLoadKind.ReFlyStart, false, false, false));
            Assert.Equal(LoadKind.DiscardReFly,
                LoadReconcilePolicy.ClassifyRefined(EarlyLoadKind.DiscardReFly, false, false, false));
        }

        [Fact]
        public void ThePlainRewindBranchRecordsItsBreakBeforeItReturns()
        {
            string body = OnLoadBody();
            string block = RewindBlock(body);

            int note = block.IndexOf(NoteLoad, StringComparison.Ordinal);
            int handler = block.IndexOf(RewindHandler, StringComparison.Ordinal);
            int ret = LastReturn(block);
            Assert.True(note >= 0,
                "timeline-break gate: the plain rewind branch of ParsekScenario.OnLoad must call "
                + NoteLoad + " (it returns before the in-session call)");
            Assert.True(note < handler && handler < ret,
                "timeline-break gate: the rewind branch records its break before HandleRewindOnLoad "
                + "and before it returns");
            string flat = Collapse(block);
            Assert.Contains("StockShadowTakeBack.NoteLoad( LoadKind.PlainRewind, "
                + "ResolvePlainRewindBreakUT(KerbalsModule.ReadLoadedSaveUT(), RewindContext.RewindSaveOwnClockUT), "
                + "CaptureSequence.CurrentHighWater());", flat);
        }

        [Fact]
        public void TheInSessionBranchRecordsTheBreakOfTheRefinedKind()
        {
            string body = OnLoadBody();
            int rewindEnd = RewindBlockStart(body) + RewindBlock(body).Length;
            int refined = IndexOrFail(body, "LoadReconcilePolicy.ClassifyRefined(");
            int quickloadDiscard = IndexOrFail(body, "DiscardStashedOnQuickload(");

            var calls = AllIndices(body, NoteLoad);
            Assert.Equal(2, calls.Count);
            int inSession = calls.Single(i => i >= rewindEnd);
            Assert.True(refined < inSession && inSession < quickloadDiscard,
                "timeline-break gate: the in-session call follows ClassifyRefined and precedes "
                + "the refined kind's consumers");
            Assert.Contains("StockShadowTakeBack.NoteLoad( refinedLoadKind, KerbalsModule.ReadLoadedSaveUT(), "
                + "CaptureSequence.CurrentHighWater());", Collapse(body.Substring(inSession)));
        }

        [Fact]
        public void OnLoadHasNoOtherReturnThatCouldSkipTheBreak()
        {
            // Every return in OnLoad is the inert game-mode gate (before any load is
            // classified), the rewind branch's, or one after the in-session call (the in-session
            // branch's own end, the cold branch); any other would let a time-travel load skip
            // both calls.
            string body = OnLoadBody();
            int early = IndexOrFail(body, "LoadReconcilePolicy.ClassifyEarly(");
            int rewindStart = RewindBlockStart(body);
            int rewindEnd = rewindStart + RewindBlock(body).Length;
            int inSessionCall = AllIndices(body, NoteLoad).Where(i => i >= rewindEnd).DefaultIfEmpty(-1).First();
            Assert.True(inSessionCall > 0, "timeline-break gate: the in-session call is missing");
            var returns = Regex.Matches(body, @"\breturn\b").Cast<Match>().Select(m => m.Index).ToList();
            Assert.NotEmpty(returns);
            foreach (int r in returns)
            {
                bool beforeClassify = r < early;
                bool inRewind = r > rewindStart && r < rewindEnd;
                bool afterInSessionCall = r > inSessionCall;
                Assert.True(beforeClassify || inRewind || afterInSessionCall,
                    "timeline-break gate: OnLoad returns at offset " + r + " outside the inert gate and "
                    + "the rewind branch, before the in-session call; a time-travel load could skip "
                    + "the timeline break there");
            }
        }

        [Fact]
        public void EveryLoadClearsTheSubjectScienceMirrorBeforeItIsClassified()
        {
            string body = OnLoadBody();
            int clear = IndexOrFail(body, "CommittedMilestoneReach.ClearSubjectScienceMirror(");
            int early = IndexOrFail(body, "LoadReconcilePolicy.ClassifyEarly(");
            int inertReturn = Regex.Match(body, @"\breturn\s*;").Index;
            Assert.True(inertReturn < clear && clear < early,
                "timeline-break gate: every non-inert load drops the subject science mirror of the "
                + "save and clock it was built at, before anything reads it");
        }

        [Fact]
        public void ResolvePlainRewindBreakUT_PrefersTheLoadedClock_ThenTheRewindSave()
        {
            Assert.Equal(50.0, ParsekScenario.ResolvePlainRewindBreakUT(50.0, 70.0));
            Assert.Equal(70.0, ParsekScenario.ResolvePlainRewindBreakUT(double.NaN, 70.0));
            Assert.True(double.IsNaN(ParsekScenario.ResolvePlainRewindBreakUT(double.NaN, double.NaN)));
        }

        // ---- helpers ----

        private static string OnLoadBody()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(repo, "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(path), "Source file not found at " + path);
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
            int sig = prepared.IndexOf("public override void OnLoad(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(sig >= 0, "timeline-break gate: OnLoad not found");
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }

        private static int RewindBlockStart(string body)
        {
            int handler = IndexOrFail(body, RewindHandler);
            int cond = body.LastIndexOf(RewindBranch, handler, StringComparison.Ordinal);
            Assert.True(cond >= 0, "timeline-break gate: the rewind branch's condition was not found");
            return body.IndexOf('{', cond);
        }

        private static string RewindBlock(string body)
        {
            string block = SourceScanText.BraceMatchedBlock(body, RewindBlockStart(body));
            Assert.Contains(RewindHandler, block);
            return block;
        }

        private static int LastReturn(string block)
        {
            var m = Regex.Matches(block, @"\breturn\s*;");
            Assert.True(m.Count > 0, "timeline-break gate: the rewind branch no longer returns");
            return m[m.Count - 1].Index;
        }

        private static List<int> AllIndices(string text, string needle)
        {
            var list = new List<int>();
            int idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                list.Add(idx);
                idx += needle.Length;
            }
            return list;
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "timeline-break gate: '" + needle + "' not found");
            return idx;
        }

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");
    }
}
