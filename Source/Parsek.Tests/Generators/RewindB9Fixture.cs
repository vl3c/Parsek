using System;
using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Assembles the B9 rewindable-tree fixture (catalog B9 / S4.1 / S1.5): a
    /// committed tree whose root ascent splits into two controllable siblings - a
    /// surviving upper stage and a CRASHED booster (<see cref="TerminalState.Destroyed"/>)
    /// - plus a <see cref="RewindPoint"/> at the split so a driver can invoke
    /// Rewind-to-Separation on a chosen slot.
    ///
    /// <para>
    /// The RP satisfies the three re-fly usability prerequisites the fixture owns
    /// (design-autotest-seam-verbs-c1.md, "B9 fixture RP-usability prerequisites"):
    /// </para>
    /// <list type="number">
    ///   <item>a fixed, known <see cref="RewindPoint.RewindPointId"/> (<see cref="RewindPointId"/>)
    ///   a spec cites verbatim, with its quicksave sidecar written to
    ///   <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c> by
    ///   <see cref="ScenarioWriter.WriteRewindPointSaveFiles"/>;</item>
    ///   <item><see cref="RewindPoint.CreatingSessionId"/> null (so
    ///   <c>LoadTimeSweep</c> keeps it as a durable split point rather than
    ///   discarding it as a session-scoped provisional);</item>
    ///   <item><see cref="RewindPoint.ChildSlots"/> / <see cref="RewindPoint.PidSlotMap"/>
    ///   keyed to the synthetic vessel persistentIds the injected recordings carry
    ///   (via <see cref="ScenarioWriter.DeriveVesselPersistentId"/>), so
    ///   <c>slot=1</c> resolves to the crashed booster's slot.</item>
    /// </list>
    ///
    /// <para>
    /// v1 fixture contract: the RP quicksave sidecar is a PURPOSE-BUILT scene state
    /// (<see cref="ScenarioWriter.WriteRewindPointSaveFiles"/>) that carries one
    /// controllable VESSEL per child slot, each stamped with the exact
    /// <c>persistentId</c> the slot's <see cref="RewindPoint.PidSlotMap"/> /
    /// <see cref="RewindPoint.RootPartPidMap"/> entry references and cloned from the
    /// host save's own command vessel when one is present (so its parts resolve in
    /// <c>PartLoader</c>). This makes the re-fly MECHANICS - the pre-load
    /// selected-slot scrub (<c>RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly</c>),
    /// the post-load <c>PostLoadStripper</c> strip, the Activate, and the merge -
    /// genuinely exercisable: the selected slot's vessel is present under its mapped
    /// pid, survives the strict strip, and is activatable. The tree / RP / sidecar
    /// pid triangle is consistent (PidSlotMap pids == sidecar VESSEL pids == the
    /// recordings' <see cref="Recording.VesselPersistentId"/>).
    /// </para>
    /// <para>
    /// The remaining honest limit is OPERATOR-VERIFIABLE, not structural: whether
    /// KSP can LOAD the cloned vessels live (duplicate part persistentIds / crew
    /// across the per-slot clones may be regenerated on load) is confirmed only when
    /// an operator boots the B9 split in FLIGHT - it is no longer an impossibility.
    /// </para>
    /// </summary>
    public static class RewindB9Fixture
    {
        /// <summary>Fixed, known RP id a scenario spec cites: <c>InvokeRewind rp=rp_b9_root</c>.</summary>
        public const string RewindPointId = "rp_b9_root";

        /// <summary>Weak link to the split BranchPoint (diagnostic only).</summary>
        public const string BranchPointId = "bp_b9_root";

        /// <summary>Pre-split ascent recording id (the tree root).</summary>
        public const string RootRecordingId = "b9-stack-root";

        /// <summary>Surviving upper-stage sibling: slot 0, the focus slot at the split.</summary>
        public const string UpperRecordingId = "b9-upper-b";

        /// <summary>Crashed booster sibling: slot 1, the re-fly target (<c>slot=1</c>).</summary>
        public const string BoosterRecordingId = "b9-booster-a";

        /// <summary>Slot index of the surviving upper stage.</summary>
        public const int UpperSlotIndex = 0;

        /// <summary>Slot index of the crashed booster - the <c>InvokeRewind slot=1</c> target.</summary>
        public const int BoosterSlotIndex = 1;

        // KSC-relative launch coordinates (flat pad terrain).
        private const double BaseLat = -0.0972;
        private const double BaseLon = -74.5577;

        /// <summary>
        /// Builds the B9 <see cref="RewindPoint"/> for the split at <paramref name="splitUt"/>.
        /// Pure: no I/O. Two controllable child slots (upper stage slot 0, crashed
        /// booster slot 1); <see cref="RewindPoint.PidSlotMap"/> maps each sibling's
        /// synthetic vessel persistentId to its slot; <see cref="RewindPoint.CreatingSessionId"/>
        /// stays null; the quicksave path points at the RP's own sidecar.
        /// </summary>
        public static RewindPoint BuildRewindPoint(double splitUt)
        {
            var rp = new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = BranchPointId,
                UT = splitUt,
                QuicksaveFilename = RecordingPaths.BuildRewindPointRelativePath(RewindPointId),
                FocusSlotIndex = UpperSlotIndex,
                // Durable staging RP awaiting re-fly: born SessionProvisional but with
                // NO CreatingSessionId, so LoadTimeSweep.IsSessionScopedProvisionalRp is
                // false and the sweep keeps it (LoadTimeSweep.cs:157-166).
                SessionProvisional = true,
                CreatingSessionId = null,
                Corrupted = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = UpperSlotIndex,
                        OriginChildRecordingId = UpperRecordingId,
                        Controllable = true,
                    },
                    new ChildSlot
                    {
                        SlotIndex = BoosterSlotIndex,
                        OriginChildRecordingId = BoosterRecordingId,
                        Controllable = true,
                    },
                },
                PidSlotMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveVesselPersistentId(UpperRecordingId)] = UpperSlotIndex,
                    [ScenarioWriter.DeriveVesselPersistentId(BoosterRecordingId)] = BoosterSlotIndex,
                },
                // Root-part fallback map, keyed to the SAME root-part pids the
                // sidecar VESSEL nodes carry (ScenarioWriter stamps each slot's
                // cloned root PART with DeriveRootPartPersistentId(recordingId)).
                // Distinct from the vessel-level pids so the strip's fallback path
                // is exercised and the pre-load scrub can match on either key.
                RootPartPidMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveRootPartPersistentId(UpperRecordingId)] = UpperSlotIndex,
                    [ScenarioWriter.DeriveRootPartPersistentId(BoosterRecordingId)] = BoosterSlotIndex,
                },
            };
            return rp;
        }

        /// <summary>
        /// Populates a v3 <see cref="ScenarioWriter"/> with the B9 committed tree
        /// (root ascent + surviving upper stage + crashed booster) and the split
        /// RewindPoint. The caller injects the writer into the fixture save; the RP
        /// quicksave sidecar is written self-referentially at inject time.
        /// </summary>
        public static void PopulateWriter(ScenarioWriter writer, double baseUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            double splitUt = baseUT + 60.0;

            writer.AddRecordingsAsTree(new[]
            {
                BuildRoot(baseUT),
                BuildUpperStage(splitUt),
                BuildBooster(splitUt),
            });

            writer.AddRewindPoint(BuildRewindPoint(splitUt));
        }

        // ---- recording builders -------------------------------------------

        // Pre-split ascent (root): a short vertical climb from the pad, ending at
        // the split UT where the stack separates.
        private static RecordingBuilder BuildRoot(double baseUT)
        {
            double t = baseUT;
            var b = new RecordingBuilder("B9 Stack")
                .WithRecordingId(RootRecordingId)
                .WithRecordingGroup("Rewind-B9");
            b.AddPoint(t,      BaseLat, BaseLon, 80);
            b.AddPoint(t + 15, BaseLat, BaseLon, 2400);
            b.AddPoint(t + 30, BaseLat, BaseLon, 9000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 22000);
            b.AddPoint(t + 60, BaseLat, BaseLon, 41000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("B9 Stack", "Jebediah Kerman", pid: 200001)
                    .AsLanded(BaseLat, BaseLon, 80));
            return b;
        }

        // Surviving upper stage (slot 0): coasts to orbit after separation.
        private static RecordingBuilder BuildUpperStage(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder("B9 Upper B")
                .WithRecordingId(UpperRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordingGroup("Rewind-B9")
                .WithTerminalState((int)TerminalState.Orbiting);
            b.AddPoint(t,      BaseLat, BaseLon, 41000);
            b.AddPoint(t + 20, BaseLat, BaseLon, 63000);
            b.AddPoint(t + 40, BaseLat, BaseLon, 78000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("B9 Upper B", pid: 200002)
                    .AsOrbiting(700000, 0.01, 6.0, 0, 0, 0));
            return b;
        }

        // Crashed booster (slot 1): falls back and impacts near the pad. Terminal
        // Destroyed = the "Crashed sibling" that a re-fly targets.
        private static RecordingBuilder BuildBooster(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder("B9 Booster A")
                .WithRecordingId(BoosterRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordingGroup("Rewind-B9")
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithTerrainHeightAtEnd(75);
            b.AddPoint(t,      BaseLat, BaseLon, 41000);
            b.AddPoint(t + 25, BaseLat, BaseLon, 18000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 3000);
            b.AddPoint(t + 55, BaseLat, BaseLon, 75);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("B9 Booster A", pid: 200003)
                    .AsLanded(BaseLat, BaseLon, 75));
            return b;
        }
    }
}
