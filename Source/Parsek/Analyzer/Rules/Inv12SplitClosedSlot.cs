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
    // model can read: MergeState.CommittedProvisional is written only by
    // RecordingStore.ApplyRewindProvisionalMergeStates over RewindPoint slot tips, so a
    // CommittedProvisional HEAD IS a slot origin whether or not the RP still exists.
    //
    // SEVERITY: WARN, deliberately, and the RED= header token must stay 0 on it
    // (AnalysisReport.IsRed reads only non-baselined FAIL / STALE-FIXTURE). Three
    // reasons, the same shape as INV11's:
    //   1. The producer no longer emits it. The carry landed 2026-09-08, so every save
    //      written by a current build is clean by construction. A FAIL would gate on
    //      HISTORY rather than on a live defect.
    //   2. Saves that predate the fix legitimately carry it, and there is no migration
    //      (the format contract forbids one). A player's pre-fix save reads the shape
    //      forever; the reap already happened and no rule can undo it.
    //   3. Baselining is structurally unavailable on the harness path: the harness
    //      verifier and the CI fixture floor both run BaselineMode.Forbid, where a
    //      baseline.cfg beside the save is itself a FAIL.
    // WARN names the damage on every save that carries it - the harness runs the
    // analyzer over every produced save, so this is the standing regression net for a
    // future producer that starts closing slots again - without gating a lane. Promote
    // to FAIL only if a produced save from a current build ever carries it, which would
    // itself be the regression.
    //
    // ONE FINDING PER CHAIN, not per member: the pair is the damage.
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

            // Chain id -> members, in the model's own order. Recordings with no ChainId
            // are unsplit and cannot carry the shape.
            var chains = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
            var chainOrder = new List<string>();
            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.ChainId))
                    continue;
                List<Recording> members;
                if (!chains.TryGetValue(rec.ChainId, out members))
                {
                    members = new List<Recording>();
                    chains[rec.ChainId] = members;
                    chainOrder.Add(rec.ChainId);
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

            foreach (string chainId in chainOrder)
            {
                List<Recording> members = chains[chainId];
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
                    Inv("INV12 split-closed-slot chain={0} head={1} headMergeState={2} "
                        + "headTerminal={3} tip={4} tipIndex={5} tipMergeState=Immutable "
                        + "tipTerminal={6} members={7} - the terminal moved to the tip without "
                        + "its MergeState, so every open/closed read on this slot answers closed",
                        chainId,
                        head.RecordingId ?? "<no-id>",
                        head.MergeState,
                        head.TerminalStateValue.HasValue
                            ? head.TerminalStateValue.Value.ToString() : "<none>",
                        tip.RecordingId ?? "<no-id>",
                        tip.ChainIndex,
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
