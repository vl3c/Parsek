using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// R10 partial: the thin Unity applier for the ADDITIVE, READ-ONLY
    /// <c>ListHandles kind=&lt;family&gt;</c> verb. It walks live objects into the plain
    /// DTO rows of <see cref="TestCommandListHandles"/> and emits that class's payload;
    /// every decision (the arg parse, the caps, the orderings, the key sequences) is
    /// there, not here.
    ///
    /// <para><b>WHY THIS VERB EXISTS.</b> Until R10 nothing a driven run could read named
    /// a LIVE object: <c>InvokeRewind rp=</c>, <c>SealSlot rp=</c>,
    /// <c>SimulateStockSwitchClick pid=</c> and <c>DeleteRecording index=</c> all take an
    /// id the spec author had to know in advance, while a live id is a fresh Guid
    /// (rewind points, recordings, trees) or a launch-assigned persistentId (vessels).
    /// This is the seam half of the answer; the harness half is the
    /// <c>${step.field}</c> substitution, which carries an enumerated id onto a later
    /// step's wire.</para>
    ///
    /// <para><b>SINGLE-PHASE and side-effect-free.</b> The whole body is a synchronous
    /// walk of in-memory state, so there is no <c>TryComplete*</c> counterpart and no
    /// <c>DeferralBudget</c> row; the default budget only ever bounds the
    /// <c>RequiresGameLoaded</c> defer. Because nothing is mutated there is no ERROR
    /// terminal either: a bad arg is REJECTED, and every reachable state of the store is
    /// a legitimate OK - including the empty one.</para>
    ///
    /// <para><b>ERS note.</b> The committed walk goes through
    /// <c>RecordingStore.CommittedTrees</c>, the un-audited tree surface the SealSlot
    /// partial already walks; the audited flat lists are never read here (nor named in
    /// prose - the gate is a substring scan and reads a comment exactly as it reads
    /// code). An enumeration is deliberately RAW anyway: a spec that then acts on a
    /// listed id must see the same object the acting verb will.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void ListHandlesImpl(ParsedCommand cmd)
        {
            string kindArg = ArgOrNull(cmd, "kind");
            ListHandlesKind kind;
            string rejectReason;
            if (!TestCommandListHandles.ParseKind(kindArg, out kind, out rejectReason))
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "listhandles rejected reason={0} kind={1}",
                    rejectReason, kindArg ?? string.Empty));
                SetExecResult("REJECTED", null, rejectReason);
                return;
            }

            List<KeyValuePair<string, string>> payload;
            switch (kind)
            {
                case ListHandlesKind.RewindPoints:
                    payload = TestCommandListHandles.BuildRewindPointsPayload(GatherRewindPointRows());
                    break;
                case ListHandlesKind.Committed:
                    payload = TestCommandListHandles.BuildCommittedPayload(GatherCommittedTreeGroups());
                    break;
                default:
                    payload = TestCommandListHandles.BuildActivePayload(GatherActiveRow());
                    break;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "listhandles kind={0} count={1} truncated={2}",
                TestCommandListHandles.KindToken(kind),
                TestCommandListHandles.CountFromPayload(payload, kind),
                TestCommandListHandles.TruncatedFromPayload(payload)));
            SetExecResult("OK", payload, null);
        }

        // ----- live walks -----

        /// <summary>
        /// Rewind points in list order, each slot's open bit resolved through the SAME
        /// lookup SealSlot uses (the slot's effective chain+supersede tip, then that id
        /// against the committed trees), so "open" here and "sealable" there cannot
        /// disagree.
        /// </summary>
        private static List<RewindPointRow> GatherRewindPointRows()
        {
            var rows = new List<RewindPointRow>();
            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null || scenario.RewindPoints == null)
                return rows;

            IReadOnlyList<RecordingSupersedeRelation> supersedes = scenario.RecordingSupersedes;
            foreach (RewindPoint rp in scenario.RewindPoints)
            {
                if (rp == null)
                    continue;
                var slots = new List<SlotRow>();
                if (rp.ChildSlots != null)
                {
                    foreach (ChildSlot slot in rp.ChildSlots)
                    {
                        if (slot == null)
                            continue;
                        string tipId = slot.EffectiveRecordingId(supersedes);
                        Recording tip = FindCommittedRecordingByIdForSeal(tipId);
                        slots.Add(new SlotRow
                        {
                            OriginChildRecordingId = slot.OriginChildRecordingId,
                            Open = TestCommandListHandles.SlotOpen(
                                tip != null,
                                tip != null ? tip.MergeState : MergeState.Immutable),
                        });
                    }
                }
                rows.Add(new RewindPointRow
                {
                    Id = rp.RewindPointId,
                    Ut = rp.UT,
                    Provisional = rp.SessionProvisional,
                    Corrupted = rp.Corrupted,
                    Slots = slots,
                });
            }
            return rows;
        }

        /// <summary>
        /// One group per committed tree, in tree list order; the builder sorts each
        /// group's recordings by id. Two pids per row: the recording's OWN
        /// <c>VesselPersistentId</c> (craft-baked, reused by every launch of the craft)
        /// and <c>SpawnedVesselPersistentId</c> (KSP-unique, 0 until a real spawn), the
        /// latter being the live handle a switch onto a spawned clone must name.
        /// </summary>
        private static List<IReadOnlyList<CommittedRow>> GatherCommittedTreeGroups()
        {
            var groups = new List<IReadOnlyList<CommittedRow>>();
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return groups;

            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null || tree.Recordings == null)
                    continue;
                var group = new List<CommittedRow>();
                foreach (KeyValuePair<string, Recording> entry in tree.Recordings)
                {
                    Recording rec = entry.Value;
                    if (rec == null)
                        continue;
                    group.Add(new CommittedRow
                    {
                        RecordingId = rec.RecordingId,
                        TreeId = tree.Id,
                        Pid = rec.VesselPersistentId,
                        SpawnedPid = rec.SpawnedVesselPersistentId,
                        Name = rec.VesselName,
                        Spawned = rec.VesselSpawned,
                        State = rec.MergeState,
                    });
                }
                groups.Add(group);
            }
            return groups;
        }

        /// <summary>
        /// The live flight tree, or the default (all-empty) row outside a live FLIGHT.
        /// The empty answer is a TRUE observation - there is no live tree - so it is an
        /// OK, not a defer: deferring would hold the FIFO head to the budget in every
        /// non-flight scene, where the answer will never change.
        /// </summary>
        private static ActiveRow GatherActiveRow()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || flight == null)
                return default(ActiveRow);

            RecordingTree tree = flight.ActiveTreeForDisplay;
            if (tree == null)
                return default(ActiveRow);

            uint activePid = 0;
            Recording activeRec = FindRecordingInTree(tree, tree.ActiveRecordingId);
            if (activeRec != null)
                activePid = activeRec.VesselPersistentId;

            var background = new List<BackgroundRow>();
            if (tree.BackgroundMap != null)
            {
                foreach (KeyValuePair<uint, string> entry in tree.BackgroundMap)
                    background.Add(new BackgroundRow { Pid = entry.Key, RecordingId = entry.Value });
            }

            return new ActiveRow
            {
                TreeId = tree.Id,
                ActiveRecordingId = tree.ActiveRecordingId,
                ActivePid = activePid,
                Background = background,
            };
        }
    }
}
