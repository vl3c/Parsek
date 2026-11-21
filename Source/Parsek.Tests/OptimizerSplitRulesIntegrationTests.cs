using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using static Parsek.Tests.OptimizerSplitShapes;

namespace Parsek.Tests
{
    /// <summary>
    /// The split rule version and row role through the paths outside the optimizer pass:
    /// the RULE 3 fold of a split piece back into a re-flown live recording
    /// (<see cref="CommittedListOptimizer.ReabsorbSplitPieceIntoLiveCopy"/>,
    /// <see cref="CommittedListOptimizer.SplitPieceFoldRefusal"/>) and the split recorder's
    /// direct commit (<see cref="TreeCommitService.CommitRecordingDirect"/>).
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitRulesIntegrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public OptimizerSplitRulesIntegrationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            Parsek.Logistics.RouteStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_srit_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            Parsek.Logistics.RouteStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        [Fact]
        public void FoldRefusal_MixedSplitRuleVersions_KeepsThePiece()
        {
            var piece = Make("piece", 2000, Atmo(300));
            piece.SplitRuleVersion = 1;
            var live = Make("live", 0, Exo(2000), Atmo(300));
            live.SplitRuleVersion = 2;

            string refusal = CommittedListOptimizer.SplitPieceFoldRefusal(piece, live);
            Assert.NotNull(refusal);
            Assert.Contains("split rules v1", refusal);

            live.SplitRuleVersion = 1;
            Assert.Null(CommittedListOptimizer.SplitPieceFoldRefusal(piece, live));
            // An unset version counts as version 1.
            live.SplitRuleVersion = 0;
            Assert.Null(CommittedListOptimizer.SplitPieceFoldRefusal(piece, live));
        }

        [Fact]
        public void Fold_ReReadsTheSurvivorsPassRoleFromItsContent()
        {
            // The live recording once ended in the air (a reentry row) and now holds the piece's
            // span too: its first pass ends back in space, so it is an aerobraking row.
            var live = Make("live", 0, Atmo(100), Exo(3000), Atmo(300), Ground(100));
            live.SplitRuleVersion = 2;
            live.SegmentRole = "reentry";
            live.TreeId = "tree-fold";
            var piece = Make("piece", 3100, Atmo(300), Ground(100));
            piece.SplitRuleVersion = 2;
            piece.SegmentRole = "reentry";
            piece.TreeId = "tree-fold";
            RecordingStore.AddRecordingWithTreeForTesting(piece);

            CommittedListOptimizer.ReabsorbSplitPieceIntoLiveCopy(live, piece, "test-fold");

            Assert.Equal("aerobraking", live.SegmentRole);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("role re-read after the fold") && l.Contains("reentry -> aerobraking"));
        }

        [Fact]
        public void CommitRecordingDirect_StampsTheCurrentSplitRules()
        {
            var rec = Make("direct", 0, Exo(100));
            Assert.Equal(0, rec.SplitRuleVersion);

            TreeCommitService.CommitRecordingDirect(rec);

            Assert.Equal(2, rec.SplitRuleVersion);
            Assert.Contains(logLines, l => l.Contains("stamped version 2 on rec=direct at direct commit"));
        }
    }
}
