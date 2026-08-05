using System;
using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Assembles the crew-loss rewindable-tree fixture (CL stage B): a committed
    /// tree whose root ascent splits into two controllable siblings - a surviving
    /// probe upper stage and a CREWED pod that is destroyed with its kerbal aboard
    /// - plus a <see cref="RewindPoint"/> at the split so a driver can invoke
    /// Rewind-to-Separation on the crewed slot.
    ///
    /// <para>
    /// It is <see cref="RewindB9Fixture"/> with ONE substantive difference, and the
    /// difference is the whole point: B9's re-fly target (slot 1) is a crewless
    /// <c>ProbeShip</c> booster, whereas this fixture's re-fly target is a CREWED
    /// pod. That is what puts a <see cref="GameActionType.KerbalAssignment"/> action
    /// carrying <see cref="KerbalEndState.Dead"/> inside the supersede subtree, which
    /// is the only way <c>SupersedeCommit.CommitTombstones</c> can be observed
    /// retiring a kerbal death. Everything else - the RP-usability prerequisites, the
    /// pid/guid triangle, the production-shaped quicksave - is B9's proven shape,
    /// reused deliberately rather than re-derived.
    /// </para>
    ///
    /// <para>
    /// THE DEATH ROW IS PRODUCT-DERIVED, NOT FIXTURE-AUTHORED, and that is the
    /// property that makes the resulting claim worth anything. This fixture authors
    /// no ledger action at all. It authors a crewed recording whose
    /// <see cref="TerminalState"/> is <see cref="TerminalState.Destroyed"/>; on load
    /// Parsek's own inference does the rest:
    /// </para>
    /// <list type="number">
    ///   <item><c>LedgerOrchestrator.NeedsCrewEndStatePopulation</c> admits the
    ///   recording through its GHOST-VISUAL crew source branch - the branch PR #1395
    ///   added precisely because a destroyed vessel has no end-of-recording
    ///   <see cref="Recording.VesselSnapshot"/>;</item>
    ///   <item><c>KerbalsModule.InferCrewEndState</c> maps
    ///   <see cref="TerminalState.Destroyed"/> to <see cref="KerbalEndState.Dead"/>;</item>
    ///   <item><c>LedgerOrchestrator.MigrateKerbalAssignments</c> derives the
    ///   <see cref="GameActionType.KerbalAssignment"/> row for the recording from that
    ///   populated end state.</item>
    /// </list>
    /// <para>
    /// So the pod carries a <see cref="Recording.GhostVisualSnapshot"/> and
    /// DELIBERATELY NO <see cref="Recording.VesselSnapshot"/>: giving it one would
    /// short-circuit <c>NeedsCrewEndStatePopulation</c> on its first branch and the
    /// fixture would stop exercising the destroyed-vessel path it exists to cover,
    /// while still producing a green-looking Dead row. The other two recordings keep
    /// vessel snapshots because they are not destroyed.
    /// </para>
    ///
    /// <para>
    /// THE POD IS THE TREE'S ONLY CREW SOURCE, and that is what makes the SECOND
    /// half of the registry's <c>dead-crew-strip</c> definition measurable at all.
    /// The cell is pinned as (i) the death action is tombstoned AND (ii) the ELS
    /// recompute that follows leaves no PERMANENT (death-derived) reservation for
    /// that kerbal - <c>permanent=0</c> in the <c>Recomputed after tombstones:</c>
    /// line, RE-PINNED 2026-08-05 off the discriminating re-fly this fixture change
    /// bought. (The 2026-08-02 wording was NAME-scoped and unsatisfiable by any
    /// same-crew re-fly: the re-flown fork always carries the kerbal alive, so its
    /// own temporary reservation legitimately survives. See the registry's D12
    /// block.) CL-3's measurement flight <c>2026-08-03_1834</c> measured (i) holding
    /// and (ii) failing, with the surviving reservation sourced from
    /// <see cref="RootRecordingId"/> - which is OUTSIDE the supersede subtree
    /// (<c>subtreeCount=1</c>, rooted at <see cref="PodRecordingId"/>), so no
    /// tombstone of the pod could ever release it. The cause was this fixture: the
    /// root carried a CREWED <c>FleaRocket</c> snapshot naming the same kerbal with
    /// no terminal state, so <c>KerbalsModule.InferCrewEndState</c> returned
    /// <see cref="KerbalEndState.Unknown"/> and the root held an indefinite
    /// (<c>endUT=Infinity</c>) reservation on him independently of the pod.
    /// The root is therefore a CREWLESS <c>ProbeShip</c> now: it models the stack,
    /// and the crew rides the pod. Do not re-crew it - a second crew source in the
    /// tree adds a confound ON TOP of the fork's own legitimate reservation, which is
    /// what made the fixture-vs-product question undecidable in the first place.
    /// Confirmed by <c>2026-08-04_2136</c>: with the root crewless, the root-sourced
    /// reservation is gone and the death-sourced one IS released.
    /// (B9's root stays crewed; B9 never asks this question.)
    /// </para>
    /// </summary>
    public static class RewindCrewLossFixture
    {
        /// <summary>Fixed, known RP id a scenario spec cites: <c>InvokeRewind rp=rp_cl_root</c>.</summary>
        public const string RewindPointId = "rp_cl_root";

        /// <summary>Weak link to the split BranchPoint (diagnostic only).</summary>
        public const string BranchPointId = "bp_cl_root";

        /// <summary>
        /// Pre-split ascent recording id (the tree root). CREWLESS - see the class
        /// docstring: the pod is the tree's only crew source.
        /// </summary>
        public const string RootRecordingId = "cl-stack-root";

        /// <summary>Surviving probe upper stage: slot 0, the focus slot at the split.</summary>
        public const string ProbeRecordingId = "cl-probe-b";

        /// <summary>
        /// CREWED pod destroyed with its kerbal aboard: slot 1, the re-fly target
        /// (<c>InvokeRewind slot=1</c>) and the recording whose derived
        /// <see cref="GameActionType.KerbalAssignment"/>+<see cref="KerbalEndState.Dead"/>
        /// row the merge must tombstone.
        /// </summary>
        public const string PodRecordingId = "cl-pod-a";

        /// <summary>
        /// The kerbal who dies aboard <see cref="PodRecordingId"/>. Stock's starting
        /// commander, so a career fixture's roster already contains him and the
        /// derived assignment row resolves a real trait rather than a synthesized one.
        /// </summary>
        public const string DeadCrewName = "Jebediah Kerman";

        /// <summary>Slot index of the surviving probe upper stage.</summary>
        public const int ProbeSlotIndex = 0;

        /// <summary>Slot index of the crewed destroyed pod - the <c>InvokeRewind slot=1</c> target.</summary>
        public const int PodSlotIndex = 1;

        // KSC-relative launch coordinates (flat pad terrain), matching B9 so both
        // fixtures' quicksaves place their slots on the same known-good ground.
        private const double BaseLat = -0.0972;
        private const double BaseLon = -74.5577;

        /// <summary>
        /// Builds the crew-loss <see cref="RewindPoint"/> for the split at
        /// <paramref name="splitUt"/>. Pure: no I/O. Two controllable child slots
        /// (probe upper stage slot 0, crewed pod slot 1);
        /// <see cref="RewindPoint.CreatingSessionId"/> stays null so
        /// <c>LoadTimeSweep</c> keeps it as a durable split point instead of
        /// discarding it as a session-scoped provisional.
        /// </summary>
        public static RewindPoint BuildRewindPoint(double splitUt)
        {
            var rp = new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = BranchPointId,
                UT = splitUt,
                QuicksaveFilename = RecordingPaths.BuildRewindPointRelativePath(RewindPointId),
                FocusSlotIndex = ProbeSlotIndex,
                SessionProvisional = true,
                CreatingSessionId = null,
                Corrupted = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = ProbeSlotIndex,
                        OriginChildRecordingId = ProbeRecordingId,
                        Controllable = true,
                    },
                    new ChildSlot
                    {
                        SlotIndex = PodSlotIndex,
                        OriginChildRecordingId = PodRecordingId,
                        Controllable = true,
                    },
                },
                PidSlotMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveVesselPersistentId(ProbeRecordingId)] = ProbeSlotIndex,
                    [ScenarioWriter.DeriveVesselPersistentId(PodRecordingId)] = PodSlotIndex,
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveRootPartPersistentId(ProbeRecordingId)] = ProbeSlotIndex,
                    [ScenarioWriter.DeriveRootPartPersistentId(PodRecordingId)] = PodSlotIndex,
                },
            };
            return rp;
        }

        /// <summary>
        /// Populates a v3 <see cref="ScenarioWriter"/> with the crew-loss committed
        /// tree (crewed root ascent + surviving probe + destroyed crewed pod) and the
        /// split RewindPoint. The caller injects the writer into the fixture save; the
        /// RP quicksave sidecar is written self-referentially at inject time.
        /// </summary>
        public static void PopulateWriter(ScenarioWriter writer, double baseUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            double splitUt = baseUT + 60.0;

            writer.AddRecordingsAsTree(new[]
            {
                BuildRoot(baseUT),
                BuildProbeUpperStage(splitUt),
                BuildCrewedPod(splitUt),
            });

            writer.AddRewindPoint(BuildRewindPoint(splitUt));
        }

        // ---- recording builders -------------------------------------------

        // Pre-split ascent (root): a short vertical climb from the pad, ending at
        // the split UT where the pod separates from the upper stage.
        //
        // CREWLESS ON PURPOSE (see the class docstring): the root models the stack and
        // the crew rides the pod, so `cl-pod-a` is the ONLY crew source in the tree and
        // its tombstone is the only thing that can release the DEATH-derived
        // reservation on DeadCrewName. (The re-flown fork holds a live TEMPORARY one of
        // its own, and that is correct - it is why half (ii) is pinned on `permanent=0`
        // rather than on the reservation count.) A crewed root snapshot here holds an
        // ADDITIONAL independent indefinite reservation from OUTSIDE the supersede
        // subtree, which is what made CL-3's 2026-08-03_1834 flight measure `Recomputed
        // after tombstones: 1 reservations remain` + a stand-in for the same kerbal,
        // with no way to tell the fixture confound from a product defect.
        private static RecordingBuilder BuildRoot(double baseUT)
        {
            double t = baseUT;
            var b = new RecordingBuilder("CL Stack")
                .WithRecordingId(RootRecordingId)
                .WithRecordingGroup("Rewind-CrewLoss");
            b.AddPoint(t,      BaseLat, BaseLon, 80);
            b.AddPoint(t + 15, BaseLat, BaseLon, 2400);
            b.AddPoint(t + 30, BaseLat, BaseLon, 9000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 22000);
            b.AddPoint(t + 60, BaseLat, BaseLon, 41000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("CL Stack", pid: 210001)
                    .AsLanded(BaseLat, BaseLon, 80));
            return b;
        }

        // Surviving probe upper stage (slot 0): coasts to orbit after separation.
        // Crewless and intact, so it contributes NO kerbal-death row - which is what
        // makes the tombstone tally attributable to the pod alone.
        private static RecordingBuilder BuildProbeUpperStage(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder("CL Probe B")
                .WithRecordingId(ProbeRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(ProbeRecordingId))
                .WithRecordingGroup("Rewind-CrewLoss")
                .WithTerminalState((int)TerminalState.Orbiting);
            b.AddPoint(t,      BaseLat, BaseLon, 41000);
            b.AddPoint(t + 20, BaseLat, BaseLon, 63000);
            b.AddPoint(t + 40, BaseLat, BaseLon, 78000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("CL Probe B", pid: 210002)
                    .AsOrbiting(700000, 0.01, 6.0, 0, 0, 0));
            return b;
        }

        // Crewed pod (slot 1): falls back and impacts near the pad with its kerbal
        // aboard. Terminal Destroyed + a GHOST-VISUAL crew source and NO vessel
        // snapshot is the production shape of a crewed vessel that was destroyed
        // (see the class docstring): it is what makes Parsek DERIVE the
        // KerbalAssignment+Dead row instead of the fixture asserting one.
        private static RecordingBuilder BuildCrewedPod(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder("CL Pod A")
                .WithRecordingId(PodRecordingId)
                .WithParentRecordingId(RootRecordingId)
                // Mirrors RewindB9Fixture's booster (R7-FIXTURE-GAPS gap 2): slot 1
                // is an OPEN Unfinished Flight only when its effective tip MergeState
                // is CommittedProvisional; the Immutable default made the slot read
                // as sealed. Inert for CL-3's armed floors - every MergeState
                // consumer on the merge path (EffectiveState visibility / closure /
                // tip walking, SupersedeCommit.AppendRelations' row-write guard)
                // special-cases only NotCommitted.
                .WithMergeState(MergeState.CommittedProvisional)
                // Launch identity agrees with the guid ScenarioWriter stamps on this
                // slot's sidecar VESSEL, so QuickloadResumeMatchGuard's
                // LaunchGuidConclusivelyDiffers cannot reject the re-fly candidate.
                // Without it the guid is backfilled from the snapshot pid and the two
                // sides conclusively differ - the documented R1 trap.
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(PodRecordingId))
                .WithRecordingGroup("Rewind-CrewLoss")
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithTerrainHeightAtEnd(75);
            b.AddPoint(t,      BaseLat, BaseLon, 41000);
            b.AddPoint(t + 25, BaseLat, BaseLon, 18000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 3000);
            b.AddPoint(t + 55, BaseLat, BaseLon, 75);
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.CrewedShip("CL Pod A", DeadCrewName, pid: 210003)
                    .AsLanded(BaseLat, BaseLon, 75));
            return b;
        }
    }
}
