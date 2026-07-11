using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV7 tree topology. Hand-built in-memory AnalyzerModel lists (ScenarioWriter
    // cannot emit supersede/tombstone rows, per plan correction C4). Pure over the
    // model: no loader, no disk, no shared static state.
    public class Inv7TreeTopologyTests
    {
        private static Recording Rec(string id, string chainId = null, int chainIndex = -1, int branch = 0)
        {
            return new Recording
            {
                RecordingId = id,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = branch,
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv7TreeTopology().Evaluate(model).ToList();
        }

        // Guards: a valid multi-branch chain (branch 0 = 0,1,2; branch 2 = 0,1)
        // with resolvable parent links -> zero FAIL. Fails if per-branch
        // contiguity is not scoped and the combined index set is treated as one
        // run (branch 2's indices would look like "gaps" against branch 0).
        [Fact]
        public void ValidMultiBranchChain_NoFindings()
        {
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording>
                {
                    Rec("a", "chainX", 0, 0),
                    Rec("b", "chainX", 1, 0),
                    Rec("c", "chainX", 2, 0),
                    Rec("d", "chainX", 0, 2),
                    Rec("e", "chainX", 1, 2),
                },
            };
            // parent links that resolve
            model.Recordings[1].ParentRecordingId = "a";
            model.Recordings[2].ParentRecordingId = "b";

            Assert.Empty(Run(model));
        }

        // Guards: a Recording.SupersedeTargetId pointing at an id absent from the
        // model -> FAIL (dangling). Fails if dangling supersede links pass, which
        // would break re-fly visibility resolution.
        [Fact]
        public void DanglingSupersedeTargetId_Fails()
        {
            var rec = Rec("a");
            rec.SupersedeTargetId = "ghost-missing";
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording> { rec },
            };

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.RuleId == Inv7TreeTopology.RuleIdConst
                && f.Level == VerdictLevel.Fail
                && f.Message.Contains("kind=dangling")
                && f.Message.Contains("ghost-missing"));
        }

        // Guards: a dangling tombstone RetiringRecordingId -> FAIL. Fails if
        // tombstone recording links are not validated against the model.
        [Fact]
        public void DanglingTombstoneRetiringRecordingId_Fails()
        {
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording> { Rec("a") },
                Tombstones = new List<LedgerTombstone>
                {
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_1",
                        ActionId = "act_1",
                        RetiringRecordingId = "nope",
                    },
                },
            };

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.Level == VerdictLevel.Fail
                && f.Message.Contains("field=RetiringRecordingId")
                && f.Message.Contains("kind=dangling"));
        }

        // Guards: a two-node parent cycle (a.parent=b, b.parent=a) -> FAIL AND the
        // walk terminates (the test itself would hang on an infinite loop). Fails
        // if the visited-set cycle guard regresses.
        [Fact]
        public void TwoNodeParentCycle_Fails_AndTerminates()
        {
            var a = Rec("a");
            var b = Rec("b");
            a.ParentRecordingId = "b";
            b.ParentRecordingId = "a";
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording> { a, b },
            };

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.RuleId == Inv7TreeTopology.RuleIdConst
                && f.Level == VerdictLevel.Fail
                && f.Message.Contains("kind=cycle"));
        }

        // Guards: a ChainIndex gap AT a supersede boundary (HEAD/TIP re-fly split)
        // -> INFO, not FAIL. The chain carries a supersede relation whose old side
        // is a chain member, so the missing index is explained. Fails if the
        // supersede-boundary exemption regresses and false-alarms on every
        // re-flown tree.
        [Fact]
        public void ChainGapAtSupersedeBoundary_IsInfo()
        {
            // Chain X branch 0 has indices 0,1,3 (2 was split into HEAD/TIP).
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording>
                {
                    Rec("a", "chainX", 0, 0),
                    Rec("b", "chainX", 1, 0),
                    Rec("tip", "chainX", 3, 0),
                },
                SupersedeRelations = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_1",
                        OldRecordingId = "b",   // a chain member is superseded
                        NewRecordingId = "tip",
                        UT = 100.0,
                    },
                },
            };

            List<Finding> findings = Run(model);

            Finding gap = Assert.Single(findings, f => f.Message.Contains("chaingap"));
            Assert.Equal(VerdictLevel.Info, gap.Level);
            Assert.Contains("missingIndex=2", gap.Message);
            Assert.Contains("supersedeExempt=True", gap.Message);
        }

        // Guards: a ChainIndex gap NOT explained by any supersede row -> FAIL.
        // Fails if the rule treats every chain gap as exempt.
        [Fact]
        public void ChainGapWithoutSupersede_Fails()
        {
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording>
                {
                    Rec("a", "chainY", 0, 0),
                    Rec("b", "chainY", 1, 0),
                    Rec("c", "chainY", 3, 0),
                },
            };

            List<Finding> findings = Run(model);

            Finding gap = Assert.Single(findings, f => f.Message.Contains("chaingap"));
            Assert.Equal(VerdictLevel.Fail, gap.Level);
            Assert.Contains("missingIndex=2", gap.Message);
            Assert.Contains("supersedeExempt=False", gap.Message);
        }

        // Guards: parallel ghost-only continuations (ChainBranch=2) with their own
        // contiguous index run pass even when the combined index set across
        // branches looks gapped. Fails if contiguity is not scoped per
        // (ChainId, ChainBranch).
        [Fact]
        public void ParallelBranchContiguousRuns_NoFindings()
        {
            var model = new AnalyzerModel
            {
                SaveName = "inv7",
                Recordings = new List<Recording>
                {
                    Rec("a", "chainZ", 0, 0),
                    Rec("b", "chainZ", 1, 0),
                    Rec("d", "chainZ", 0, 2),
                    Rec("e", "chainZ", 1, 2),
                },
            };

            Assert.Empty(Run(model).Where(f => f.Message.Contains("chaingap")));
        }
    }
}
