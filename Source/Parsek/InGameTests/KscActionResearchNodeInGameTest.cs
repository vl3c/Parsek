using System.Collections.Generic;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-scene coverage for the <c>KscAction research-node</c> applier
    /// (<see cref="TestCommandKscAction"/>). The pure xUnit suite proves the accept /
    /// typed-refusal decision core but is STRUCTURALLY BLIND to the applier: whether the
    /// real stock research-buy path actually spends science and unlocks the node depends on
    /// a correctly-hosted <c>RDTech</c> MonoBehaviour, which only exists inside a live KSP
    /// game. A host-less <c>new RDTech</c> shell (the pre-fix bug) would silently spend NO
    /// science, leave the node unresearched, fail the effect-confirm (a false REJECTED on a
    /// valid research), AND fire a phantom zero-cost <c>OnTechnologyResearched</c> into the
    /// recorder - none of which the pure suite can see.
    ///
    /// <para>This test drives the REAL applier against a real cheap unresearched node and
    /// asserts the three things the fix guarantees: the verb reports OK, science was spent,
    /// the node is now Available in R&amp;D, and the <c>GameStateRecorder</c> observer saw
    /// the (real, non-phantom) tech-research event. Career + FLIGHT only; skips on non-career
    /// per the house rules. Self-restoring (science pool restored in finally) with
    /// <c>RestoreBatchFlightBaselineAfterExecution=true</c> as the persisted-state backstop
    /// that reverts the actual node unlock.</para>
    ///
    /// <para>Covers the design's Test Plan in-game section for M-C1 research-node.</para>
    /// </summary>
    public class KscActionResearchNodeInGameTest
    {
        private const string Tag = "KscActionResearchInGame";

        [InGameTest(Category = "TestCommands", Scene = GameScenes.FLIGHT,
            Description = "KscAction research-node drives the real hosted RDTech buy: the verb "
                + "returns OK, science is spent, the node becomes Available, and the "
                + "GameStateRecorder observer records the real tech-research event.",
            RestoreBatchFlightBaselineAfterExecution = true)]
        public void ResearchNode_SpendsScience_UnlocksNode_RecorderObserves()
        {
            if (!PreconditionsMet(out string skip))
            {
                InGameAssert.Skip(skip);
                return;
            }

            if (!TryFindCheapUnresearchedNode(out string node, out float cost))
            {
                InGameAssert.Skip("No affordable-once-topped-up unresearched tech node found to drive");
                return;
            }

            float scienceBefore = ResearchAndDevelopment.Instance.Science;

            // Top the pool up so the node is affordable, tracking exactly what we add so the
            // finally can restore the original balance.
            float added = 0f;
            using (SuppressionGuard.Resources())
            {
                float target = cost + 100f;
                if (scienceBefore < target)
                {
                    added = target - scienceBefore;
                    ResearchAndDevelopment.Instance.AddScience(added, TransactionReasons.None);
                }
            }

            var captured = new List<string>();
            var prevObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => captured.Add(line);

            try
            {
                float scienceAtExecute = ResearchAndDevelopment.Instance.Science;

                TestCommandKscAction.KscActionExecOutcome outcome =
                    TestCommandKscAction.Execute("research-node", node, null, null);

                InGameAssert.AreEqual("OK", outcome.Verdict,
                    "KscAction research-node must report OK on a valid affordable node "
                    + "(a host-less RDTech shell would false-REJECT with blocked-committed). "
                    + "msg=" + (outcome.Msg ?? "<null>"));

                InGameAssert.IsTrue(
                    ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available,
                    "The node '" + node + "' must be Available in R&D after the real research buy");

                float scienceAfter = ResearchAndDevelopment.Instance.Science;
                InGameAssert.IsTrue(scienceAfter < scienceAtExecute,
                    "The real research buy must SPEND science (before=" + scienceAtExecute
                    + ", after=" + scienceAfter + ", cost=" + cost
                    + "); a host-less RDTech would spend nothing");

                bool observed = false;
                foreach (string line in captured)
                {
                    if (line != null
                        && line.IndexOf("[GameStateRecorder]", System.StringComparison.Ordinal) >= 0
                        && line.IndexOf("TechResearched", System.StringComparison.Ordinal) >= 0
                        && line.IndexOf("'" + node + "'", System.StringComparison.Ordinal) >= 0)
                    {
                        observed = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(observed,
                    "The GameStateRecorder observer must record the real TechResearched event "
                    + "for '" + node + "' (a null-host RDTech would fire a phantom Successful "
                    + "event, but the effect-confirm would have REJECTED it before OK)");

                ParsekLog.Info(Tag,
                    "ResearchNode_SpendsScience_UnlocksNode_RecorderObserves: passed (node="
                    + node + ", spent=" + (scienceAtExecute - scienceAfter) + ").");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                using (SuppressionGuard.Resources())
                {
                    if (ResearchAndDevelopment.Instance != null)
                    {
                        float d = scienceBefore - ResearchAndDevelopment.Instance.Science;
                        if (Mathf.Abs(d) > 0.01f)
                            ResearchAndDevelopment.Instance.AddScience(d, TransactionReasons.None);
                    }
                }
            }
        }

        private static bool PreconditionsMet(out string skipReason)
        {
            skipReason = null;
            if (HighLogic.CurrentGame == null
                || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                skipReason = "KscAction research-node in-game test is career-only";
                return false;
            }
            if (ResearchAndDevelopment.Instance == null || AssetBase.RnDTechTree == null)
            {
                skipReason = "R&D singleton / tech tree not initialized";
                return false;
            }
            if (RecordingStore.HasPendingTree
                || GameStateRecorder.HasActiveUncommittedTree()
                || GameStateRecorder.HasLiveRecorder())
            {
                skipReason = "A live/pending tree would perturb the recorder - stop recording "
                    + "and commit/discard any pending tree first";
                return false;
            }
            return true;
        }

        // Find an unresearched node with a positive, modest science cost we can top up to and
        // afford. Kept modest so the temporary science top-up stays small.
        private static bool TryFindCheapUnresearchedNode(out string node, out float cost)
        {
            node = null;
            cost = 0f;
            ProtoRDNode[] nodes = AssetBase.RnDTechTree.GetTreeNodes();
            if (nodes == null) return false;

            float best = float.MaxValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                ProtoRDNode n = nodes[i];
                if (n == null || n.tech == null) continue;
                string id = n.tech.techID;
                if (string.IsNullOrEmpty(id)) continue;
                if (ResearchAndDevelopment.GetTechnologyState(id) == RDTech.State.Available) continue;
                float c = n.tech.scienceCost;
                if (c <= 0f) continue;
                if (c < best)
                {
                    best = c;
                    node = id;
                    cost = c;
                }
            }
            return node != null;
        }
    }
}
