using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Pure result of <see cref="TestCommandStashSlot.ResolveTarget"/>.</summary>
    internal struct StashTargetSelection
    {
        internal string RewindPointId;
        internal int SlotIndex;

        /// <summary>The REJECTED msg token, or null when the target parsed.</summary>
        internal string RejectReason;

        internal bool Ok => RejectReason == null;
    }

    /// <summary>
    /// Pure decision half of the <c>StashSlot</c> seam verb (the SEVENTH strict promotion
    /// out of the M-A2 reserved list: the wire token is byte-identical before and after,
    /// only the response changes). The Unity applier
    /// (<c>ParsekTestCommandAddon.StashSlot.cs</c>) owns the scenario / store reads and the
    /// <c>UnfinishedFlightStashHandler.TryStash</c> call; everything decidable without KSP
    /// lives here so it is xUnit-covered.
    ///
    /// <para><b>What it reproduces.</b> The per-row Stash button in the Recordings table's
    /// Re-Fly column (<c>RecordingsTableUI.HandleStashUnfinishedFlightClick</c>), which the
    /// table draws only on a stable Rewind Point leaf the default Unfinished Flights
    /// predicate excluded, and which calls <c>UnfinishedFlightStashHandler.TryStash</c>:
    /// set the monotonic <c>ChildSlot.Stashed</c> bit and open the slot by demoting its
    /// effective chain+supersede tip <c>Immutable -&gt; CommittedProvisional</c>. After it the
    /// row reads as an Unfinished Flight and offers Fly (the re-fly, which the seam drives
    /// as <c>InvokeRewind</c>).</para>
    ///
    /// <para><b>One addressing spelling: <c>rp=</c> + <c>slot=</c></b>, <c>SealSlot</c>'s slot
    /// mode verbatim, including its <c>unknown-rp</c> / <c>unknown-slot</c> vocabulary and
    /// the "an absent slot is just an unresolvable target" choice. There is no
    /// <c>tree=</c> mode: stashing is a per-slot player intent, and a tree-wide stash would
    /// open slots no player asked for.</para>
    /// </summary>
    internal static class TestCommandStashSlot
    {
        /// <summary>No <c>rp=</c> arg was supplied.</summary>
        internal const string TargetArgMissingReason = TestCommandSealSlot.TargetArgMissingReason;

        /// <summary>An absent / unparseable / negative / unmatched slot index.</summary>
        internal const string UnknownSlotReason = TestCommandSealSlot.UnknownSlotReason;

        /// <summary>The named rewind point does not exist.</summary>
        internal const string UnknownRpReason = TestCommandSealSlot.UnknownRpReason;

        /// <summary>
        /// The production handler (or the tip lookup in front of it) declined; the
        /// handler's own reason rides as the compound tail, for example
        /// <c>alreadyStashed</c>, <c>alreadyUnfinishedFlight</c>, <c>downstreamBp</c>,
        /// <c>evaCrewJoinedForeignVessel</c>, <c>unsafeTerminal:Recovered</c>,
        /// <c>recordingAction:...</c> or the seam's own <c>tip-unresolvable</c>.
        /// ERROR, <c>SealSlot</c>'s <c>seal-refused</c> row: the
        /// verb reached the production path and something that is not the seam declined.
        /// </summary>
        internal const string StashRefusedReason = "stash-refused";

        /// <summary>
        /// The handler reported success but the ADDRESSED slot is not stashed. The handler
        /// resolves its slot from the recording it is handed, so a tip that also belongs to
        /// another rewind point's slot could land the stash elsewhere. Post-act: ERROR.
        /// </summary>
        internal const string StashResolvedOtherSlotReason = "stash-resolved-other-slot";

        /// <summary>
        /// The stash succeeded but the read-back says the row is still not an Unfinished
        /// Flight, so the player would see no Fly button. A product inconsistency between
        /// the stash resolver and the Unfinished Flights predicate; the predicate's own
        /// reason rides as the tail. Post-act: ERROR.
        /// </summary>
        internal const string StashNotUnfinishedReason = "stash-not-unfinished";

        /// <summary>No <c>ParsekScenario.Instance</c> (the SealSlot / InvokeRewind row).</summary>
        internal const string NoScenarioReason = "no-scenario";

        /// <summary>
        /// Decide which slot a <c>StashSlot</c> command addresses. Pure. A <c>tree=</c> arg
        /// is not read at all (unknown keys are ignored by the envelope's contract).
        /// </summary>
        internal static StashTargetSelection ResolveTarget(string rpArg, string slotArg)
        {
            if (string.IsNullOrEmpty(rpArg))
                return new StashTargetSelection { RejectReason = TargetArgMissingReason };

            int slotIndex;
            if (string.IsNullOrEmpty(slotArg)
                || !int.TryParse(slotArg, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out slotIndex)
                || slotIndex < 0)
            {
                return new StashTargetSelection { RejectReason = UnknownSlotReason };
            }

            return new StashTargetSelection
            {
                RewindPointId = rpArg,
                SlotIndex = slotIndex,
            };
        }

        /// <summary>
        /// The compound ERROR msg for a handler refusal: <c>stash-refused &lt;reason&gt;</c>,
        /// the handler's reason verbatim (an empty / null one reads <c>unknown</c>). Pure.
        /// </summary>
        internal static string RefusalMessage(string handlerReason)
        {
            return StashRefusedReason + " "
                + (string.IsNullOrEmpty(handlerReason) ? "unknown" : handlerReason);
        }

        /// <summary>
        /// Terminal verdict after the handler returned: OK only when the handler
        /// succeeded, the addressed slot now carries the Stashed bit, and the tip reads as
        /// an Unfinished Flight. Returns null on OK, otherwise the ERROR msg. Pure.
        /// </summary>
        internal static string ClassifyAfterStash(
            bool handlerOk, string handlerReason, bool addressedSlotStashed,
            bool tipIsUnfinishedFlight, string unfinishedReason)
        {
            if (!handlerOk)
                return RefusalMessage(handlerReason);
            if (!addressedSlotStashed)
                return StashResolvedOtherSlotReason;
            if (!tipIsUnfinishedFlight)
                return StashNotUnfinishedReason + " "
                    + (string.IsNullOrEmpty(unfinishedReason) ? "unknown" : unfinishedReason);
            return null;
        }
    }
}
