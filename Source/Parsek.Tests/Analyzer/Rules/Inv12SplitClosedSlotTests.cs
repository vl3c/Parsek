using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV12 split-closed slot: a chain whose HEAD is CommittedProvisional while its
    // terminal-carrying TIP is Immutable. Pure over the model, so every cell here builds
    // recordings in memory - no loader, no filesystem, no RecordingStore statics.
    //
    // Each cell names the shape it pins. The positive is the exact byte pattern the
    // harvested subject of the defect freezes (harness/fixtures/saves/refly-a-recorded:
    // HEAD 32ca5546... CommittedProvisional at chainIndex 0, TIP 8da7c2c2... with
    // terminalState = 4 and NO mergeState key, which loads as the Immutable default).
    public class Inv12SplitClosedSlotTests
    {
        private const string ChainId = "chain_1848a736";

        private static Recording Rec(
            string id, int chainIndex, MergeState mergeState, TerminalState? terminal,
            string chainId = ChainId, int chainBranch = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                MergeState = mergeState,
                TerminalStateValue = terminal,
            };
        }

        private static AnalyzerModel Model(
            IEnumerable<Recording> recordings,
            IEnumerable<RecordingSupersedeRelation> supersedes = null)
        {
            return new AnalyzerModel
            {
                SaveName = "inv12-test",
                Recordings = recordings.ToList(),
                Trees = new List<RecordingTree>(),
                SupersedeRelations = (supersedes ?? Enumerable.Empty<RecordingSupersedeRelation>()).ToList(),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv12SplitClosedSlot().Evaluate(model).ToList();
        }

        // ---------- The shape ------------------------------------------------

        [Fact]
        public void CpHeadWithImmutableTerminalCarryingTip_Warns()
        {
            var model = Model(new[]
            {
                Rec("32ca5546", 0, MergeState.CommittedProvisional, TerminalState.SubOrbital),
                Rec("8da7c2c2", 1, MergeState.Immutable, TerminalState.Destroyed),
            });

            List<Finding> findings = Run(model);

            Finding f = Assert.Single(findings);
            Assert.Equal("INV12-SPLIT-CLOSED-SLOT", f.RuleId);
            Assert.Equal(VerdictLevel.Warn, f.Level);
            // Target is the HEAD: that is the id a slot's OriginChildRecordingId names,
            // and the id a reader has to go looking for.
            Assert.Equal("32ca5546", f.Target);
            Assert.Contains("head=32ca5546", f.Message);
            Assert.Contains("tip=8da7c2c2", f.Message);
            Assert.Contains("tipMergeState=Immutable", f.Message);
            Assert.Contains("tipTerminal=Destroyed", f.Message);
            Assert.Contains("chain=" + ChainId, f.Message);
            Assert.Contains("branch=0", f.Message);
            // The message must state the ambiguity rather than assert the defect:
            // an ordinary Seal writes the same bytes (see the rule's class comment).
            Assert.Contains("or somebody sealed the slot", f.Message);
            Assert.False(string.IsNullOrEmpty(f.CitedContract));
        }

        [Fact]
        public void TheFindingIsWarn_SoTheRunIsNotRed()
        {
            var model = Model(new[]
            {
                Rec("head", 0, MergeState.CommittedProvisional, TerminalState.SubOrbital),
                Rec("tip", 1, MergeState.Immutable, TerminalState.Destroyed),
            });

            AnalysisReport report = InvariantEvaluator.Evaluate(
                model, new IRecordingInvariant[] { new Inv12SplitClosedSlot() });

            Assert.Equal(1, report.Counts.Warn);
            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(0, report.Counts.StaleFixture);
            // The .analysis.txt header's terminal RED= token is written from IsRed;
            // a WARN must leave it 0 or every RVR-shaped lane reds on history.
            Assert.False(report.IsRed);
        }

        [Fact]
        public void ThreeMemberChain_HeadCpAndLastMemberSealed_Warns()
        {
            // The head/tip pair is taken by ChainIndex, not by list position: the model's
            // Recordings list is flat across trees and carries no ordering promise.
            var model = Model(new[]
            {
                Rec("mid", 1, MergeState.CommittedProvisional, null),
                Rec("tip", 2, MergeState.Immutable, TerminalState.Destroyed),
                Rec("head", 0, MergeState.CommittedProvisional, null),
            });

            Finding f = Assert.Single(Run(model));
            Assert.Equal("head", f.Target);
            Assert.Contains("tip=tip", f.Message);
            Assert.Contains("members=3", f.Message);
        }

        // ---------- The negatives --------------------------------------------

        [Fact]
        public void HealthySplit_TipCarriesTheHeadsCommittedProvisional_IsSilent()
        {
            // What PR #1658's carry produces: the tip is born CommittedProvisional, the
            // slot stays open, and nothing here is damage.
            var model = Model(new[]
            {
                Rec("head", 0, MergeState.CommittedProvisional, null),
                Rec("tip", 1, MergeState.CommittedProvisional, TerminalState.Destroyed),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void GenuinelySealedChain_HeadImmutableToo_IsSilent()
        {
            // A slot somebody actually sealed, or a chain that never belonged to an RP
            // slot at all. Both halves Immutable is the ordinary state of most chains in
            // any save, so a rule that fired here would fire on nearly everything.
            var model = Model(new[]
            {
                Rec("head", 0, MergeState.Immutable, null),
                Rec("tip", 1, MergeState.Immutable, TerminalState.Destroyed),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void UnsplitSlot_SingleMemberChain_IsSilent()
        {
            var model = Model(new[]
            {
                Rec("solo", 0, MergeState.CommittedProvisional, TerminalState.Destroyed),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void UnsplitSlot_NoChainIdAtAll_IsSilent()
        {
            var model = Model(new[]
            {
                Rec("standalone_a", -1, MergeState.CommittedProvisional, TerminalState.Destroyed,
                    chainId: null),
                Rec("standalone_b", -1, MergeState.Immutable, TerminalState.Destroyed,
                    chainId: null),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void ImmutableTipWithoutATerminal_IsSilent()
        {
            // The tip must be the terminal CARRIER for its MergeState to be the seal the
            // slot reads. A terminal-less tip is a still-open segment, not a closed slot.
            var model = Model(new[]
            {
                Rec("head", 0, MergeState.CommittedProvisional, TerminalState.SubOrbital),
                Rec("tip", 1, MergeState.Immutable, null),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void NotCommittedTip_IsSilent()
        {
            // NotCommitted is a live recorder, which every open/closed read treats as
            // OPEN. Only Immutable closes a slot.
            var model = Model(new[]
            {
                Rec("head", 0, MergeState.CommittedProvisional, null),
                Rec("tip", 1, MergeState.NotCommitted, TerminalState.Destroyed),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void SupersededTip_IsSilent()
        {
            // After a Re-Fly merge the slot's effective tip is the FORK, not this chain,
            // so this chain's MergeState is not what any slot reads.
            var model = Model(
                new[]
                {
                    Rec("head", 0, MergeState.CommittedProvisional, null),
                    Rec("tip", 1, MergeState.Immutable, TerminalState.Destroyed),
                },
                new[]
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_1",
                        OldRecordingId = "tip",
                        NewRecordingId = "fork",
                    },
                });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void TwoDamagedChains_ProduceOneFindingEach_AndAHealthyThirdIsSilent()
        {
            var model = Model(new[]
            {
                Rec("a_head", 0, MergeState.CommittedProvisional, null, chainId: "chain_a"),
                Rec("a_tip", 1, MergeState.Immutable, TerminalState.Destroyed, chainId: "chain_a"),
                Rec("b_head", 0, MergeState.CommittedProvisional, null, chainId: "chain_b"),
                Rec("b_tip", 1, MergeState.Immutable, TerminalState.Landed, chainId: "chain_b"),
                Rec("c_head", 0, MergeState.CommittedProvisional, null, chainId: "chain_c"),
                Rec("c_tip", 1, MergeState.CommittedProvisional, TerminalState.Landed, chainId: "chain_c"),
            });

            List<Finding> findings = Run(model);

            Assert.Equal(2, findings.Count);
            Assert.Equal(new[] { "a_head", "b_head" }, findings.Select(f => f.Target).ToArray());
        }

        // ---------- Degenerate inputs ----------------------------------------

        [Fact]
        public void NullModelAndNullRecordings_AreSilentAndDoNotThrow()
        {
            Assert.Empty(new Inv12SplitClosedSlot().Evaluate(null));
            Assert.Empty(new Inv12SplitClosedSlot().Evaluate(new AnalyzerModel { Recordings = null }));
        }

        [Fact]
        public void NullMembersInTheList_AreSkipped()
        {
            var model = Model(new List<Recording>
            {
                null,
                Rec("head", 0, MergeState.CommittedProvisional, null),
                null,
                Rec("tip", 1, MergeState.Immutable, TerminalState.Destroyed),
            });

            Finding f = Assert.Single(Run(model));
            Assert.Equal("head", f.Target);
        }

        // ---------- The ambiguity, pinned ------------------------------------

        [Fact]
        public void SealedSlot_HeadStaysCommittedProvisional_ProducesTheSameShape_AndIsReportedAsAmbiguous()
        {
            // NOT a false positive to be fixed - a structural collision, pinned so nobody
            // "tightens" the rule into asserting the defect. UnfinishedFlightSealHandler
            // flips ONLY the slot's effective chain TIP to Immutable and no code path
            // anywhere demotes the HEAD, so a player who seals a slot whose recording was
            // split leaves EXACTLY the bytes the pre-2026-09-08 carry defect left. There
            // is no on-disk discriminator: a seal is stored as nothing but the tip's
            // MergeState.
            var model = Model(new[]
            {
                Rec("sealed_head", 0, MergeState.CommittedProvisional, null),
                Rec("sealed_tip", 1, MergeState.Immutable, TerminalState.Landed),
            });

            Finding f = Assert.Single(Run(model));
            Assert.Equal(VerdictLevel.Warn, f.Level);
            Assert.Contains("either the terminal moved to the tip without its MergeState", f.Message);
            Assert.Contains("or somebody sealed the slot", f.Message);
            Assert.Contains("nothing on disk tells the two apart", f.Message);
        }

        // ---------- Chain branches -------------------------------------------

        [Fact]
        public void AHeadAndATipOnDifferentChainBranches_AreNotPaired()
        {
            // The tip walk this rule models refuses to cross ChainBranch
            // (EffectiveState skips a candidate whose ChainBranch differs), and
            // ChainSegmentManager writes ChainBranch = 1 for ghost-only parallel
            // continuations. Keying on ChainId alone would pair these two and report a
            // slot nobody reads.
            var model = Model(new[]
            {
                Rec("b0_head", 0, MergeState.CommittedProvisional, null, chainBranch: 0),
                Rec("b1_tip", 1, MergeState.Immutable, TerminalState.Destroyed, chainBranch: 1),
            });

            Assert.Empty(Run(model));
        }

        [Fact]
        public void EachChainBranchIsEvaluatedOnItsOwn()
        {
            // Branch 0 carries the shape; branch 1 of the SAME ChainId is a healthy split.
            // One finding, and it names branch 0.
            var model = Model(new[]
            {
                Rec("b0_head", 0, MergeState.CommittedProvisional, null, chainBranch: 0),
                Rec("b0_tip", 1, MergeState.Immutable, TerminalState.Destroyed, chainBranch: 0),
                Rec("b1_head", 0, MergeState.CommittedProvisional, null, chainBranch: 1),
                Rec("b1_tip", 1, MergeState.CommittedProvisional, TerminalState.Landed, chainBranch: 1),
            });

            Finding f = Assert.Single(Run(model));
            Assert.Equal("b0_head", f.Target);
            Assert.Contains("branch=0", f.Message);
        }

        [Fact]
        public void TheRuleIsRegisteredInBothTheFullSetAndTheInGamePureCoreSubset()
        {
            Assert.Contains(InvariantRegistry.AllRules,
                r => r.RuleId == "INV12-SPLIT-CLOSED-SLOT");
            // Pure over the model, so H5's live-store walk gets it for free.
            Assert.Contains(InvariantRegistry.InGamePureCoreRules,
                r => r.RuleId == "INV12-SPLIT-CLOSED-SLOT");
        }
    }
}
