using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the single-phase <c>StashSlot</c> verb (the SEVENTH
    /// strict reserved-name promotion since M-C1 - the wire token is byte-identical before
    /// and after, only the response changes).
    ///
    /// <para><b>WHY THIS VERB EXISTS.</b> A stable separation leaf the default Unfinished
    /// Flights predicate excludes - the focus slot that ended Orbiting, a Landed or
    /// Splashed stage - can be re-flown only after the player opens it with the per-row
    /// Stash button. No unattended run could press it, so a re-fly of such a slot was
    /// unreachable live: RF-18 proves the slot's walk through its own EVA and re-board,
    /// but its pod stack is exactly that kind of leaf and so could never be re-flown.
    /// This verb is that button.</para>
    ///
    /// <para><b>It drives the production path, not a field poke.</b> The stash goes through
    /// <c>UnfinishedFlightStashHandler.TryStash</c>, which is what the button's click
    /// handler calls: it re-runs the stash resolver the table used to decide the button
    /// exists (visibility, slot resolution, the not-yet-stashed guard, the Unfinished
    /// Flights verdict that must be a stash override, the terminal and retry-blocking
    /// action gates), sets <c>ChildSlot.Stashed</c> + <c>StashedRealTime</c>, demotes the
    /// slot's effective tip to <c>CommittedProvisional</c> with its sidecars dirty, and
    /// bumps <c>ParsekScenario.BumpSupersedeStateVersionLive()</c>. The button persists
    /// nothing, and neither does the verb: a lane that reloads from disk after it takes an
    /// explicit <c>SaveGame</c> step, exactly as a player would have to save.</para>
    ///
    /// <para><b>The recording it hands the handler is the slot's EFFECTIVE tip</b>, the one
    /// the handler demotes and <c>SealSlot</c>'s slot mode hands <c>TrySeal</c>. It is always
    /// visible (nothing supersedes the end of a supersede walk), so it is a row the player
    /// could click.</para>
    ///
    /// <para><b>SINGLE-PHASE with a read-back.</b> The stash is synchronous; the verb then
    /// checks that the ADDRESSED slot carries the Stashed bit and that the slot's row now
    /// reads as an Unfinished Flight through the same predicate the table's Fly button
    /// reads (<c>UnfinishedFlightClassifier.IsVisibleUnfinishedFlight</c>) - on the slot's
    /// origin when it is still visible, else on the walked tip, because the Unfinished
    /// Flights list draws one row per slot on that anchor. An OK means "the player would
    /// now see Fly on this slot's row", and <c>unfinishedRow=</c> names the row.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void StashSlotImpl(ParsedCommand cmd)
        {
            string rpArg = ArgOrNull(cmd, "rp");
            string slotArg = ArgOrNull(cmd, "slot");

            StashTargetSelection sel = TestCommandStashSlot.ResolveTarget(rpArg, slotArg);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "stashslot start rp={0} slot={1}",
                rpArg ?? string.Empty, slotArg ?? string.Empty));

            if (!sel.Ok)
            {
                ParsekLog.Warn(Tag, $"stashslot rejected reason={sel.RejectReason}");
                SetExecResult("REJECTED", null, sel.RejectReason);
                return;
            }

            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn(Tag, "stashslot no-scenario");
                SetExecResult("ERROR", null, TestCommandStashSlot.NoScenarioReason);
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
                    $"stashslot rejected reason={TestCommandStashSlot.UnknownRpReason} rp={sel.RewindPointId}");
                SetExecResult("REJECTED", null, TestCommandStashSlot.UnknownRpReason);
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
                    "stashslot rejected reason={0} rp={1} slot={2}",
                    TestCommandStashSlot.UnknownSlotReason, sel.RewindPointId, sel.SlotIndex));
                SetExecResult("REJECTED", null, TestCommandStashSlot.UnknownSlotReason);
                return;
            }

            string tipId = slot.EffectiveRecordingId(scenario.RecordingSupersedes);
            Recording tip = FindCommittedRecordingByIdForSeal(tipId);
            if (tip == null)
            {
                ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                    "stashslot tip-unresolvable rp={0} slot={1} tip={2}",
                    sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>"));
                SetExecResult("ERROR", null,
                    TestCommandStashSlot.RefusalMessage("tip-unresolvable"));
                return;
            }

            MergeState tipStateBefore = tip.MergeState;
            string handlerReason;
            bool ok = UnfinishedFlightStashHandler.TryStash(tip, out handlerReason);

            // The Fly button draws on ONE row per slot: the slot's origin when it is still
            // visible, else the walked tip (EffectiveState.TryResolveUnfinishedFlight's
            // anchor dedupe). Read back both, origin first, and report which one it is.
            string unfinishedReason = null;
            string unfinishedRowId = null;
            bool unfinished = false;
            if (ok)
            {
                Recording origin = FindCommittedRecordingByIdForSeal(slot.OriginChildRecordingId);
                string originReason = null;
                if (origin != null
                    && UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(origin, out originReason))
                {
                    unfinished = true;
                    unfinishedRowId = origin.RecordingId;
                }
                else if (UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(tip, out unfinishedReason))
                {
                    unfinished = true;
                    unfinishedRowId = tip.RecordingId;
                }
                else if (string.IsNullOrEmpty(unfinishedReason))
                {
                    unfinishedReason = originReason;
                }
            }

            string errorMsg = TestCommandStashSlot.ClassifyAfterStash(
                ok, handlerReason, slot.Stashed, unfinished, unfinishedReason);

            var payload = Payload(
                Kv("rp", sel.RewindPointId ?? string.Empty),
                Kv("slot", Int(sel.SlotIndex)),
                Kv("tip", tipId ?? string.Empty),
                Kv("stashed", Bool(slot.Stashed)),
                Kv("tipMergeStateBefore", tipStateBefore.ToString()),
                Kv("tipMergeState", tip.MergeState.ToString()),
                Kv("unfinishedFlight", Bool(unfinished)),
                Kv("unfinishedRow", unfinishedRowId ?? string.Empty));

            if (errorMsg != null)
            {
                // A handler refusal changed nothing (every TryStash reject precedes its
                // first write) and is the same outcome the player gets as a screen
                // message, so it is a Warn. Only a stash that WROTE and then failed its
                // read-back is an Error: that is a product inconsistency.
                string line = string.Format(CultureInfo.InvariantCulture,
                    "stashslot {0} rp={1} slot={2} tip={3} msg={4}",
                    ok ? "readback-failed" : "refused",
                    sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>", errorMsg);
                if (ok)
                    ParsekLog.Error(Tag, line);
                else
                    ParsekLog.Warn(Tag, line);
                SetExecResult("ERROR", payload, errorMsg);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "stashslot complete rp={0} slot={1} tip={2} tipMergeState={3}->{4} " +
                "stashed=true unfinishedFlight=true unfinishedRow={5}",
                sel.RewindPointId, sel.SlotIndex, tipId ?? "<no-tip>",
                tipStateBefore, tip.MergeState, unfinishedRowId ?? "<none>"));
            SetExecResult("OK", payload, null);
        }
    }
}
