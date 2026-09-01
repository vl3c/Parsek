using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which addressing spelling a <c>SealSlot</c> command used.</summary>
    internal enum SealTargetMode
    {
        /// <summary>Neither spelling resolved (the command is a REJECTED).</summary>
        None = 0,

        /// <summary>The D9 slot spelling: <c>rp=&lt;rewindPointId&gt; slot=&lt;index&gt;</c>.</summary>
        Slot = 1,

        /// <summary>The tree spelling: <c>tree=&lt;treeId&gt;</c>.</summary>
        Tree = 2,
    }

    /// <summary>Pure result of <see cref="TestCommandSealSlot.ResolveTarget"/>.</summary>
    internal struct SealTargetSelection
    {
        internal SealTargetMode Mode;
        internal string TreeId;
        internal string RewindPointId;
        internal int SlotIndex;

        /// <summary>The REJECTED msg token, or null when the target parsed.</summary>
        internal string RejectReason;

        internal bool Ok => RejectReason == null && Mode != SealTargetMode.None;
    }

    /// <summary>
    /// Pure decision half of the <c>SealSlot</c> seam verb (the FIFTH strict promotion
    /// out of the M-A2 reserved list). The Unity applier
    /// (<c>ParsekTestCommandAddon.SealSlot.cs</c>) owns the
    /// <c>UnfinishedFlightSealHandler.TrySeal</c> calls and the store reads; everything
    /// decidable without KSP lives here so it is xUnit-covered.
    ///
    /// <para><b>Two addressing spellings, one verb.</b> The reserved NAME is a slot verb
    /// (the D9 unfinished-flights lifecycle, beside <c>StashSlot</c> / <c>FlySlot</c>),
    /// so <c>rp=</c> + <c>slot=</c> is kept and reuses <c>InvokeRewind</c>'s
    /// <c>unknown-rp</c> / <c>unknown-slot</c> vocabulary verbatim. The CONSUMER that
    /// forced the promotion is tree-scoped, though: route candidacy requires EVERY
    /// recording in a tree to be <c>MergeState.Immutable</c>
    /// (<c>RouteCandidateFinder.IsTreeFullySealed</c>), and a fixture typically carries
    /// more than one open provisional, so <c>tree=</c> seals the whole tree in one
    /// command. Both spellings drive the SAME production per-recording call.</para>
    ///
    /// <para><b>rp= wins when both are supplied</b>, mirroring
    /// <c>SimulateStockSwitchClick</c>'s <c>pid=</c>-beats-<c>vessel=</c> rule and for
    /// the same reason: a caller who supplies both gets the precise selector rather
    /// than a refusal to debug.</para>
    /// </summary>
    internal static class TestCommandSealSlot
    {
        /// <summary>Neither <c>rp=</c> nor <c>tree=</c> was supplied.</summary>
        internal const string TargetArgMissingReason = "target-arg-missing";

        /// <summary>An absent / unparseable / unmatched slot index. Deliberately the
        /// same token <c>InvokeRewind</c> uses, and deliberately NOT a separate
        /// missing-arg reason (that verb's documented choice).</summary>
        internal const string UnknownSlotReason = "unknown-slot";

        /// <summary>The named rewind point does not exist.</summary>
        internal const string UnknownRpReason = "unknown-rp";

        /// <summary>The named tree is not in the committed set.</summary>
        internal const string UnknownTreeReason = "unknown-tree";

        /// <summary>A tree-mode pass finished with recordings still not Immutable.
        /// ERROR rather than REJECTED: by then the verb has ACTED (earlier members may
        /// already be sealed and the game persisted), and the seam's split puts a
        /// post-side-effect terminal on ERROR.</summary>
        internal const string SealIncompleteReason = "seal-incomplete";

        /// <summary>A slot-mode seal that the production handler declined; the
        /// handler's own reason rides as the compound tail.</summary>
        internal const string SealRefusedReason = "seal-refused";

        /// <summary>
        /// The <see cref="SealIncompleteReason"/> tail when NOTHING refused: every
        /// remaining member got a SUCCESSFUL <c>TrySeal</c> that closed some OTHER
        /// recording, because the handler seals a recording's slot's EFFECTIVE
        /// chain+supersede tip rather than the recording itself. Such a member is
        /// structurally unclosable by the Seal action (the UI button has the identical
        /// residue), so the tree cannot be made a route candidate by sealing alone -
        /// which is a fixture-shape answer, not a handler failure, and must not read as
        /// one.
        /// </summary>
        internal const string MemberNotSlotTipReason = "member-not-slot-tip";

        /// <summary>
        /// The tail for an incomplete tree-mode pass: the first handler refusal when
        /// one happened, otherwise <see cref="MemberNotSlotTipReason"/>. Never
        /// <c>"unknown"</c> - "nothing refused and something is still open" is a
        /// KNOWN state with a name, and calling it unknown sends an operator hunting a
        /// failure that never happened. Pure.
        /// </summary>
        internal static string ResolveIncompleteReason(string firstFailureReason)
        {
            return string.IsNullOrEmpty(firstFailureReason)
                ? MemberNotSlotTipReason
                : firstFailureReason;
        }

        /// <summary>
        /// Decide which target a <c>SealSlot</c> command addresses. Pure.
        /// </summary>
        internal static SealTargetSelection ResolveTarget(string rpArg, string slotArg, string treeArg)
        {
            if (!string.IsNullOrEmpty(rpArg))
            {
                int slotIndex;
                if (string.IsNullOrEmpty(slotArg)
                    || !int.TryParse(slotArg, NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out slotIndex)
                    || slotIndex < 0)
                {
                    return new SealTargetSelection { RejectReason = UnknownSlotReason };
                }
                return new SealTargetSelection
                {
                    Mode = SealTargetMode.Slot,
                    RewindPointId = rpArg,
                    SlotIndex = slotIndex
                };
            }

            if (!string.IsNullOrEmpty(treeArg))
                return new SealTargetSelection { Mode = SealTargetMode.Tree, TreeId = treeArg };

            return new SealTargetSelection { RejectReason = TargetArgMissingReason };
        }

        /// <summary>
        /// The recording ids in <paramref name="tree"/> that are NOT yet
        /// <see cref="MergeState.Immutable"/>, in the tree's own enumeration order.
        /// Null slots are skipped for the reason <c>IsTreeFullySealed</c> skips them:
        /// a null slot cannot prove un-sealed. Pure (RecordingTree is plain data).
        /// </summary>
        internal static List<string> RecordingIdsNeedingSeal(RecordingTree tree)
        {
            var ids = new List<string>();
            if (tree == null || tree.Recordings == null)
                return ids;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null)
                    continue;
                if (rec.MergeState != MergeState.Immutable)
                    ids.Add(rec.RecordingId ?? kvp.Key);
            }
            return ids;
        }

        /// <summary>
        /// How many recordings in <paramref name="tree"/> are still not Immutable.
        /// Pure; the post-pass remaining count that decides the terminal verdict.
        /// </summary>
        internal static int CountUnsealed(RecordingTree tree)
        {
            return RecordingIdsNeedingSeal(tree).Count;
        }

        /// <summary>
        /// Terminal verdict for a completed tree-mode pass: OK when nothing is left
        /// open, ERROR (<see cref="SealIncompleteReason"/>) when something is. Pure.
        /// </summary>
        internal static string ClassifyTreeSealVerdict(int remaining)
        {
            return remaining > 0 ? "ERROR" : "OK";
        }

        /// <summary>
        /// True when the pass had nothing to do - the RVR-2 "no-op guard" shape: an
        /// already-sealed tree must answer OK, never a reject. Pure.
        /// </summary>
        internal static bool IsAlreadySealed(int sealedCount, int remaining)
        {
            return sealedCount == 0 && remaining == 0;
        }
    }
}
