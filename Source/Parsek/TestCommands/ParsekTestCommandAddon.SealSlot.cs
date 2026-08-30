using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Logistics-lane partial: the thin Unity applier for the single-phase
    /// <c>SealSlot</c> verb (the FIFTH strict reserved-name promotion since M-C1 - the
    /// wire token is byte-identical before and after, only the response changes).
    ///
    /// <para><b>WHY THIS VERB EXISTS.</b> Route candidacy is gated on a FULLY SEALED
    /// tree: <c>RouteCandidateFinder.IsTreeFullySealed</c> requires every recording in
    /// the tree to be <c>MergeState.Immutable</c>, and a flight whose terminals are
    /// flight-class leaves open provisionals behind. Nothing an unattended run could
    /// reach closed them - the Seal action is a per-row button in the Unfinished
    /// Flights UI - so H35's route fixture is documented as deliberately NOT a
    /// candidate and end-to-end route CREATION was un-automatable
    /// (ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH). This verb is that button.</para>
    ///
    /// <para><b>It drives the production path, not a field poke.</b> Every seal goes
    /// through <c>UnfinishedFlightSealHandler.TrySeal</c>, which is what the UI button
    /// calls: it resolves the recording's rewind point + child slot, flips the slot's
    /// EFFECTIVE chain+supersede tip to Immutable, marks the tip's sidecars dirty,
    /// bumps <c>ParsekScenario.BumpSupersedeStateVersionLive()</c> (the ERS-cache
    /// invalidation AND the <c>RouteStore.RevalidateSources</c> call, in one), persists
    /// the game, and only then reaps a now-orphaned rewind point. A seam that flipped
    /// the enum itself would skip all five and leave a route candidacy sweep reading a
    /// stale cache.</para>
    ///
    /// <para><b>SINGLE-PHASE.</b> TrySeal and its persist are synchronous, so the
    /// read-back (recount the tree) is a final answer and there is nothing to wait for -
    /// no <c>TryComplete*</c> counterpart, no <c>DeferralBudget</c> row.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void SealSlotImpl(ParsedCommand cmd)
        {
            string rpArg = ArgOrNull(cmd, "rp");
            string slotArg = ArgOrNull(cmd, "slot");
            string treeArg = ArgOrNull(cmd, "tree");

            SealTargetSelection sel =
                TestCommandSealSlot.ResolveTarget(rpArg, slotArg, treeArg);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "sealslot start mode={0} rp={1} slot={2} tree={3}",
                sel.Mode, rpArg ?? string.Empty, slotArg ?? string.Empty,
                treeArg ?? string.Empty));

            if (!sel.Ok)
            {
                ParsekLog.Warn(Tag, $"sealslot rejected reason={sel.RejectReason}");
                SetExecResult("REJECTED", null, sel.RejectReason);
                return;
            }

            if (sel.Mode == SealTargetMode.Slot)
                SealOneSlot(sel);
            else
                SealWholeTree(sel.TreeId);
        }

        // ----- tree mode (the route-candidacy consumer) -----

        private void SealWholeTree(string treeId)
        {
            RecordingTree tree = FindCommittedTreeByIdForSeal(treeId);
            if (tree == null)
            {
                ParsekLog.Warn(Tag,
                    $"sealslot rejected reason={TestCommandSealSlot.UnknownTreeReason} tree={treeId}");
                SetExecResult("REJECTED", null, TestCommandSealSlot.UnknownTreeReason);
                return;
            }

            int total = tree.Recordings != null ? tree.Recordings.Count : 0;
            List<string> pending = TestCommandSealSlot.RecordingIdsNeedingSeal(tree);
            int sealedCount = 0;
            int failed = 0;
            string firstFailureReason = null;

            for (int i = 0; i < pending.Count; i++)
            {
                Recording rec = FindRecordingInTree(tree, pending[i]);
                // A prior TrySeal may already have closed this one (the handler seals
                // the slot's EFFECTIVE tip, which need not be the recording passed in),
                // so re-read rather than trusting the snapshot.
                if (rec == null || rec.MergeState == MergeState.Immutable)
                    continue;

                string reason;
                if (UnfinishedFlightSealHandler.TrySeal(rec, out reason))
                {
                    sealedCount++;
                    continue;
                }
                failed++;
                if (firstFailureReason == null)
                    firstFailureReason = reason ?? "unknown";
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "sealslot member-refused tree={0} rec={1} reason={2}",
                    treeId, pending[i] ?? "<no-id>", reason ?? "unknown"));
            }

            // WHY THE INCOMPLETE REASON IS NOT "unknown". TrySeal never seals the
            // recording it is handed - it flips the recording's slot's EFFECTIVE
            // chain+supersede tip. So a member that is still open after a SUCCESSFUL
            // TrySeal is a member that is not its own slot's tip, and nothing in this
            // pass (or in the production handler) will ever close it: it is
            // structurally unclosable by the Seal action rather than refused by it.
            // `failed == 0 && remaining > 0` is exactly that case - every remaining
            // member DID get a TrySeal (the re-read above only skips ones already
            // Immutable) and every one of them succeeded on some other recording - so
            // it earns its own reason token instead of borrowing a refusal's.
            // CommitTree's chain-tip demotion (RecordingStore.ApplyRewindProvisional-
            // MergeStates) is how a tree acquires such a member.
            int remaining = TestCommandSealSlot.CountUnsealed(tree);
            bool alreadySealed = TestCommandSealSlot.IsAlreadySealed(sealedCount, remaining);
            string verdict = TestCommandSealSlot.ClassifyTreeSealVerdict(remaining);

            var payload = Payload(
                Kv("tree", treeId ?? string.Empty),
                Kv("mode", "tree"),
                Kv("total", Int(total)),
                Kv("sealed", Int(sealedCount)),
                Kv("failed", Int(failed)),
                Kv("remaining", Int(remaining)),
                Kv("alreadySealed", Bool(alreadySealed)),
                Kv("fullySealed", Bool(remaining == 0)));

            if (verdict == "OK")
            {
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "sealslot complete mode=tree tree={0} total={1} sealed={2} " +
                    "remaining=0 alreadySealed={3}",
                    treeId, total, sealedCount, alreadySealed));
                SetExecResult("OK", payload, null);
                return;
            }

            string incompleteReason =
                TestCommandSealSlot.ResolveIncompleteReason(firstFailureReason);
            ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                "sealslot incomplete tree={0} total={1} sealed={2} failed={3} " +
                "remaining={4} firstReason={5}",
                treeId, total, sealedCount, failed, remaining, incompleteReason));
            SetExecResult("ERROR", payload,
                TestCommandSealSlot.SealIncompleteReason + " " + incompleteReason);
        }

        // ----- slot mode (the D9 unfinished-flights lifecycle spelling) -----

        private void SealOneSlot(SealTargetSelection sel)
        {
            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn(Tag, "sealslot no-scenario");
                SetExecResult("ERROR", null, "no-scenario");
                return;
            }

            RewindPoint rp = null;
            if (scenario.RewindPoints != null)
            {
                foreach (RewindPoint candidate in scenario.RewindPoints)
                {
                    if (candidate != null && candidate.RewindPointId == sel.RewindPointId)
                    {
                        rp = candidate;
                        break;
                    }
                }
            }
            if (rp == null)
            {
                ParsekLog.Warn(Tag,
                    $"sealslot rejected reason={TestCommandSealSlot.UnknownRpReason} rp={sel.RewindPointId}");
                SetExecResult("REJECTED", null, TestCommandSealSlot.UnknownRpReason);
                return;
            }

            ChildSlot slot = null;
            if (rp.ChildSlots != null)
            {
                foreach (ChildSlot s in rp.ChildSlots)
                {
                    if (s != null && s.SlotIndex == sel.SlotIndex)
                    {
                        slot = s;
                        break;
                    }
                }
            }
            if (slot == null)
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "sealslot rejected reason={0} rp={1} slot={2}",
                    TestCommandSealSlot.UnknownSlotReason, sel.RewindPointId, sel.SlotIndex));
                SetExecResult("REJECTED", null, TestCommandSealSlot.UnknownSlotReason);
                return;
            }

            string tipId = slot.EffectiveRecordingId(scenario.RecordingSupersedes);
            Recording tip = FindCommittedRecordingByIdForSeal(tipId);
            if (tip == null)
            {
                // Mirrors the handler's own hard failure: with no resolvable tip there
                // is nothing to flip, and reporting success would leave the slot open
                // while the response claimed "sealed".
                ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                    "sealslot tip-unresolvable rp={0} slot={1} tip={2}",
                    sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>"));
                SetExecResult("ERROR", null,
                    TestCommandSealSlot.SealRefusedReason + " tip-unresolvable");
                return;
            }

            bool wasImmutable = tip.MergeState == MergeState.Immutable;
            string reason;
            bool ok = UnfinishedFlightSealHandler.TrySeal(tip, out reason);

            var payload = Payload(
                Kv("mode", "slot"),
                Kv("rp", sel.RewindPointId ?? string.Empty),
                Kv("slot", Int(sel.SlotIndex)),
                Kv("tip", tipId ?? string.Empty),
                Kv("sealed", Int(ok && !wasImmutable ? 1 : 0)),
                Kv("alreadySealed", Bool(wasImmutable)),
                Kv("fullySealed", Bool(tip.MergeState == MergeState.Immutable)));

            if (!ok)
            {
                ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                    "sealslot refused rp={0} slot={1} tip={2} reason={3}",
                    sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>",
                    reason ?? "unknown"));
                SetExecResult("ERROR", payload,
                    TestCommandSealSlot.SealRefusedReason + " " + (reason ?? "unknown"));
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "sealslot complete mode=slot rp={0} slot={1} tip={2} alreadySealed={3}",
                sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>", wasImmutable));
            SetExecResult("OK", payload, null);
        }

        // ----- lookups -----
        //
        // Both walk RecordingStore.CommittedTrees, which is the un-audited surface: the
        // ERS/ELS grep gate covers the raw committed-recording and ledger-action lists,
        // and a tree walk reads neither. (Do NOT restate those two member names here in
        // prose either - the gate is a substring scan and reads a comment exactly as it
        // reads code.) Sealing is deliberately a RAW-state operation anyway: it MUTATES
        // the state ERS is computed FROM.

        private static RecordingTree FindCommittedTreeByIdForSeal(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        private static Recording FindRecordingInTree(RecordingTree tree, string recordingId)
        {
            if (tree?.Recordings == null || string.IsNullOrEmpty(recordingId))
                return null;
            Recording rec;
            return tree.Recordings.TryGetValue(recordingId, out rec) ? rec : null;
        }

        private static Recording FindCommittedRecordingByIdForSeal(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return null;
            for (int i = 0; i < trees.Count; i++)
            {
                Recording hit = FindRecordingInTree(trees[i], recordingId);
                if (hit != null)
                    return hit;
            }
            return null;
        }
    }
}
