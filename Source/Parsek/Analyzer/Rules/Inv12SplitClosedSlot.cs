using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Analyzer.Rules
{
    // INV12 split-closed slot.
    //
    // THE SHAPE. A chain whose HEAD is CommittedProvisional but whose terminal-carrying
    // TIP is Immutable. It is the on-disk residue of the defect PR #1658 fixed
    // (OPTIMIZER-SPLIT-DROPS-MERGESTATE-AND-CLOSES-AN-OPEN-REFLY-SLOT):
    // MergeDialog.MergeCommit promotes a re-flyable slot's tip to CommittedProvisional
    // and only THEN runs RecordingStore.RunOptimizationPass(); when the phase-change
    // split cut the just-promoted recording, RecordingOptimizer.
    // TransferTerminalFieldsToSecondHalf moved the terminal onto a brand-new chain TIP
    // but not the MergeState, and Recording.MergeState defaults to Immutable. A slot's
    // open/closed bit is read from that tip (UnfinishedFlightClassifier.
    // IsSlotEffectiveTipOpen -> ChildSlot.EffectiveRecordingId -> EffectiveState.
    // EffectiveTipRecordingId), so the slot read closed (sealedTipClosed), drew no
    // Unfinished Flights row, and RewindPointReaper deleted its rewind-point quicksave
    // permanently.
    //
    // WHY THE RULE IS CHAIN-SHAPED RATHER THAN RewindPoint-SHAPED. The damage this rule
    // names DESTROYS the evidence a RewindPoint-keyed rule would need: the reaper removes
    // the RP, so the harvested subject of the defect
    // (harness/fixtures/saves/refly-a-recorded) carries no REWIND_POINTS node at all.
    // AnalyzerModel does not load scenario RewindPoints either (see Inv9RewindPoint's
    // class comment). The chain HEAD/TIP pair is the surviving witness and the one the
    // model can read. MergeState.CommittedProvisional is MINTED FOR A SLOT ORIGIN only by
    // RecordingStore.ApplyRewindProvisionalMergeStates (:1260 / :1290) over RewindPoint
    // slot tips; the other writers PROPAGATE an existing one rather than mint it
    // (RecordingOptimizer.MergeInto :866 and the split carry :1407,
    // UnfinishedFlightStashHandler :85 re-opening a tip, ParsekScenario.HydrationRepair).
    // So a CommittedProvisional HEAD traces back to a slot origin whether or not the RP
    // still exists.
    //
    // THE SHAPE IS AMBIGUOUS, AND THAT IS WHY IT IS ONLY A REPORT. An ordinary player
    // Seal produces THE SAME PAIR on a correct post-fix save:
    // UnfinishedFlightSealHandler.cs:97 flips ONLY the slot's effective chain TIP to
    // Immutable, and nothing anywhere demotes the HEAD that
    // ApplyRewindProvisionalMergeStates promoted (a grep of every `MergeState =` writer in
    // Source/Parsek finds no head demotion at all). LoadTimeSweep.cs:362's
    // missing-quicksave conclusion and the M-A2 SealSlot seam verb close a slot the same
    // tip-only way. There is NO on-disk discriminator: a seal is STORED as nothing but the
    // tip's MergeState, and Recording carries no seal marker. So on a chain of two or more
    // segments, "the split-carry defect's residue" and "a slot somebody sealed" are the
    // same bytes, and the finding says so rather than asserting the defect.
    //
    // SEVERITY: WARN, and the RED= header token must stay 0 on it (AnalysisReport.IsRed
    // reads only non-baselined FAIL / STALE-FIXTURE). Four reasons:
    //   1. The ambiguity above. A FAIL would red every save carrying a sealed
    //      multi-segment slot, which is a correct state a player reaches by clicking a
    //      button, and R7c's own payload produces it.
    //   2. The defect's producer no longer emits it. The carry landed 2026-09-08, so no
    //      current build creates the damaged half of the ambiguity.
    //   3. Saves that predate the fix legitimately carry that half, and there is no
    //      migration (the format contract forbids one). The reap already happened and no
    //      rule can undo it.
    //   4. Baselining is structurally unavailable on the harness path: the harness
    //      verifier and the CI fixture floor both run BaselineMode.Forbid, where a
    //      baseline.cfg beside the save is itself a FAIL.
    // What it buys despite the ambiguity: the harness runs the analyzer over every
    // produced save, so a lane that seals nothing and starts printing this line has
    // regressed the carry. Do NOT promote it to FAIL without an on-disk discriminator for
    // the seal, which today does not exist.
    //
    // ONE FINDING PER CHAIN, not per member: the pair is the subject.
    //
    // Pure over the model; reads Recordings and SupersedeRelations only. Adding this
    // rule does NOT bump AnalyzerVersion (that moves only on an .analysis.json schema
    // change; rules are data inside findings).
    internal sealed class Inv12SplitClosedSlot : IRecordingInvariant
    {
        internal const string SplitClosedSlotRuleId = "INV12-SPLIT-CLOSED-SLOT";

        public string RuleId => SplitClosedSlotRuleId;

        public string CitedContract =>
            "RecordingOptimizer.TransferTerminalFieldsToSecondHalf / UnfinishedFlightClassifier.IsSlotEffectiveTipOpen";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            // (ChainId, ChainBranch) -> members, in the model's own order. Recordings with
            // no ChainId are unsplit and cannot carry the shape.
            //
            // ChainBranch is part of the key because the tip walk this rule models REFUSES
            // to cross branches (EffectiveState.cs:1115 / :1166 both skip a candidate whose
            // ChainBranch differs), ChainSegmentManager.cs:577 really does write
            // ChainBranch = 1 for ghost-only parallel continuations, and the sibling rule
            // Inv7TreeTopology keys on the same pair. Grouping on ChainId alone would let a
            // branch-0 head pair with a branch-1 member no slot ever reads, and would hide
            // the real branch-0 tip behind it.
            var chains = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
            var chainOrder = new List<string>();
            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.ChainId))
                    continue;
                string key = rec.ChainId + "\u0000"
                    + rec.ChainBranch.ToString(CultureInfo.InvariantCulture);
                List<Recording> members;
                if (!chains.TryGetValue(key, out members))
                {
                    members = new List<Recording>();
                    chains[key] = members;
                    chainOrder.Add(key);
                }
                members.Add(rec);
            }

            // A superseded recording's effective tip is the fork, not this chain, so its
            // MergeState is not what any slot reads. Exclude those chains rather than
            // report a shape nobody consults.
            var supersededIds = new HashSet<string>(StringComparer.Ordinal);
            if (model.SupersedeRelations != null)
            {
                foreach (RecordingSupersedeRelation rel in model.SupersedeRelations)
                {
                    if (rel != null && !string.IsNullOrEmpty(rel.OldRecordingId))
                        supersededIds.Add(rel.OldRecordingId);
                }
            }

            foreach (string chainKey in chainOrder)
            {
                List<Recording> members = chains[chainKey];
                if (members.Count < 2)
                    continue; // unsplit: nothing carried anywhere

                Recording head = null;
                Recording tip = null;
                foreach (Recording rec in members)
                {
                    if (head == null || rec.ChainIndex < head.ChainIndex) head = rec;
                    if (tip == null || rec.ChainIndex > tip.ChainIndex) tip = rec;
                }
                if (head == null || tip == null || ReferenceEquals(head, tip))
                    continue;

                if (head.MergeState != MergeState.CommittedProvisional)
                    continue; // no open slot origin here (a genuinely sealed chain)
                if (tip.MergeState != MergeState.Immutable)
                    continue; // healthy split: the carry gave the tip the head's state
                if (!tip.TerminalStateValue.HasValue)
                    continue; // the tip is not the terminal carrier, so it is not the seal

                if (!string.IsNullOrEmpty(tip.RecordingId) && supersededIds.Contains(tip.RecordingId))
                    continue; // a re-fly merge moved the effective tip off this chain

                findings.Add(new Finding(
                    SplitClosedSlotRuleId,
                    VerdictLevel.Warn,
                    head.RecordingId,
                    -1,
                    Inv("INV12 split-closed-slot chain={0} branch={1} head={2} "
                        + "headMergeState={3} headTerminal={4} tip={5} tipIndex={6} "
                        + "tipMergeState={7} tipTerminal={8} members={9} - this slot reads "
                        + "CLOSED because its terminal-carrying tip is Immutable while its "
                        + "head is still open; either the terminal moved to the tip without "
                        + "its MergeState (the pre-2026-09-08 optimizer-split carry defect) "
                        + "or somebody sealed the slot, and nothing on disk tells the two "
                        + "apart",
                        head.ChainId ?? "<none>",
                        head.ChainBranch,
                        head.RecordingId ?? "<no-id>",
                        head.MergeState,
                        head.TerminalStateValue.HasValue
                            ? head.TerminalStateValue.Value.ToString() : "<none>",
                        tip.RecordingId ?? "<no-id>",
                        tip.ChainIndex,
                        tip.MergeState,
                        tip.TerminalStateValue.Value,
                        members.Count),
                    "RecordingOptimizer.TransferTerminalFieldsToSecondHalf"));
            }

            return findings;
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
