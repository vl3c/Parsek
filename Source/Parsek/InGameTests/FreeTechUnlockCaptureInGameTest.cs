using System;
using System.Collections.Generic;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live coverage for <see cref="FreeTechUnlockCapturePatch"/> (todo
    /// FREE-TECH-NODE-LOST-AFTER-REWIND). The xUnit suite proves the capture decision and the
    /// recorder core, but not that Harmony really reaches the stock
    /// <c>ResearchAndDevelopment.UnlockProtoTechNode</c>, nor that the prefix / postfix read
    /// the real R&amp;D state correctly. This test calls the stock method on a locked node with
    /// the capture's subscribers swapped for a probe (so nothing reaches the career's recorder
    /// or ledger), asserts one capture for the node, asserts a second call on the now-Available
    /// node captures nothing, and restores the node's proto entry in a finally.
    /// </summary>
    public class FreeTechUnlockCaptureInGameTest
    {
        private const string Tag = "FreeTechUnlockInGame";

        [InGameTest(Category = "FreeTechUnlock", Scene = GameScenes.SPACECENTER,
            Description = "Stock UnlockProtoTechNode on a locked node is captured once by the Parsek "
                + "patch (probe subscriber, career untouched); a repeat call on the Available node "
                + "captures nothing; the node is restored afterwards.")]
        public void UnlockProtoTechNode_LockedNode_IsCapturedOnce()
        {
            var rnd = ResearchAndDevelopment.Instance;
            if (rnd == null)
            {
                InGameAssert.Skip("requires a career or science game with the R&D singleton");
                return;
            }
            var tree = AssetBase.RnDTechTree;
            ProtoTechNode[] techs = tree != null ? tree.GetTreeTechs() : null;
            var protoNodes = KspStatePatcher.GetProtoTechNodesDictionary();
            if (techs == null || protoNodes == null)
            {
                InGameAssert.Skip("requires the stock tech tree and the R&D proto dictionary");
                return;
            }

            ProtoTechNode target = null;
            for (int i = 0; i < techs.Length; i++)
            {
                var t = techs[i];
                if (t == null || string.IsNullOrEmpty(t.techID)) continue;
                var live = rnd.GetTechState(t.techID);
                if (live == null || live.state != RDTech.State.Available)
                {
                    target = t;
                    break;
                }
            }
            if (target == null)
            {
                InGameAssert.Skip("every tech node is already researched");
                return;
            }

            string techId = target.techID;
            ProtoTechNode original;
            bool hadEntry = protoNodes.TryGetValue(techId, out original);
            int eventsBefore = GameStateStore.Events.Count;
            int rowsBefore = Ledger.Actions.Count;
            var captures = new List<FreeTechUnlock>();

            try
            {
                using (FreeTechUnlockCapturePatch.SwapSubscribersForTesting(u => captures.Add(u)))
                {
                    rnd.UnlockProtoTechNode(target);

                    var after = rnd.GetTechState(techId);
                    InGameAssert.IsTrue(after != null && after.state == RDTech.State.Available,
                        $"stock UnlockProtoTechNode must make '{techId}' Available");
                    InGameAssert.AreEqual(1, captures.Count,
                        $"the patch must capture the free unlock of '{techId}' exactly once");
                    InGameAssert.AreEqual(techId, captures[0].TechId, "captured tech id");

                    rnd.UnlockProtoTechNode(target);
                    InGameAssert.AreEqual(1, captures.Count,
                        "a repeat unlock of an Available node must not be captured again");
                }

                InGameAssert.AreEqual(eventsBefore, GameStateStore.Events.Count,
                    "the probe must keep the capture off the career's event store");
                InGameAssert.AreEqual(rowsBefore, Ledger.Actions.Count,
                    "the probe must keep the capture off the career's ledger");

                ParsekLog.Info(Tag,
                    $"UnlockProtoTechNode_LockedNode_IsCapturedOnce: tech='{techId}' captured once, " +
                    $"parts={(captures[0].PartNames != null ? captures[0].PartNames.Count : 0)}, repeat skipped");
            }
            finally
            {
                if (hadEntry)
                    protoNodes[techId] = original;
                else
                    protoNodes.Remove(techId);
                ParsekLog.Verbose(Tag,
                    $"restored proto entry for '{techId}' (hadEntry={hadEntry})");
            }
        }
    }
}
