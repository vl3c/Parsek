using System;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The AUTOMERGE-ON-BY-DEFAULT fixture (docs/dev/todo-and-known-bugs.md): a save
    /// carrying a NON-COMMITTED, NON-<see cref="PendingTreeState.Finalized"/> recording
    /// tree, authored under the production <c>isActive</c> RECORDING_TREE marker.
    ///
    /// <para><b>Why the marker is the whole fixture.</b> Every other injected fixture
    /// writes plain COMMITTED tree nodes. Those never reach
    /// <c>ParsekScenario.AutoCommitPendingTreeOutsideFlight</c> at all, and a tree
    /// written under <c>isPending</c> reaches it only as <c>Finalized</c> — because
    /// <c>RecordingStore.RestorePendingTreeFromSave</c> hard-sets that state — which
    /// takes the SILENT FULL-FIDELITY branch. The <c>isActive</c> marker is the ONLY
    /// on-disk shape that restores non-Finalized: <c>TryRestoreActiveTreeNode</c>
    /// stashes it <c>Limbo</c> (ActiveRecordingId present) or <c>LimboVesselSwitch</c>
    /// (absent), and production writes exactly this shape from
    /// <c>ParsekScenario.SavePendingTreeIfAny</c>'s Limbo branch — which, unlike
    /// <c>SaveActiveTreeIfAny</c>, carries NO scene guard.</para>
    ///
    /// <para><b>What a run of it measured, and what it measures now.</b> Booted at a
    /// NON-FLIGHT scene with <c>autoMerge</c> ON (which since the 2026-08-27 settings
    /// clamp is every player, always), the OnLoad reaches the auto-commit site with
    /// <c>pendingState=Limbo</c>. Runs `2026-08-29_1025` (S0.9, cold) and `_1043`
    /// (S0.10, warm) both took the GHOST-ONLY branch and nulled every authored
    /// <c>VesselSnapshot</c> — the repro that produced the fix. Post-fix the same
    /// fixture measures the LimboPreservingFullFidelity route instead. It does NOT by
    /// itself claim a player produces this save.</para>
    ///
    /// <para><b>Every recording carries a VesselSnapshot on purpose.</b> The
    /// preserved/released/nulled counts in the auto-commit's log line are the fidelity
    /// measurement, and a snapshot-less fixture would report 0 and prove nothing in
    /// either direction.</para>
    ///
    /// <para><b>The four leaves, and what each one's SHAPE proves.</b> None of them is
    /// filler; the fixture is a decision table.</para>
    /// <list type="bullet">
    /// <item><b>root</b> (<c>Landed</c> snapshot, no terminal state) — the pre-separation
    /// ascent, and incidentally the first null-terminal leaf: it maps LANDED -> Landed,
    /// so it stays spawnable.</item>
    /// <item><b>child</b> (<c>Orbiting</c> TERMINAL state) — a finalized-SHAPED recording,
    /// i.e. exactly the leaf the full-fidelity branch keeps spawn-at-end eligible. It is
    /// the contrast against the ghost-only branch, and it is deliberately NOT the shape a
    /// real stash produces (see the next two).</item>
    /// <item><b>coast</b> (no terminal state, <c>ORBITING</c> situation) — the GENUINE
    /// Limbo shape. <c>StashActiveTreeAsPendingLimbo</c> runs no finalize pass and stamps
    /// no terminal state, so a fixture whose only spawnable leaf carried one never
    /// exercised the null-terminal branch the fidelity fix depends on. Preserved and
    /// spawnable.</item>
    /// <item><b>escape</b> (no terminal state, <c>ESCAPING</c> situation) — the same
    /// genuine shape on the REJECTING side, and the leaf that measures the un-finalized
    /// situation gate on a live flight. The finalize path maps ESCAPING -> SubOrbital,
    /// which is not spawnable, so this leaf is ghost-only'd: snapshot released, ghost
    /// visual kept. Without the gate it would read spawnable, so this is the one leaf
    /// whose outcome DIFFERS between a build that has the gate and one that does
    /// not.</item>
    /// </list>
    /// </summary>
    public static class PendingLimboTreeFixture
    {
        public const string TreeMarkerKey = "isActive";

        public const string RootRecordingId = "limbo_root_2f7a41c8";
        public const string ChildRecordingId = "limbo_child_9b3e05d1";
        public const string CoastRecordingId = "limbo_coast_5d1c73a2";
        public const string EscapeRecordingId = "limbo_escape_e40b6cf7";

        public const string RootVesselName = "Limbo Stack";
        public const string ChildVesselName = "Limbo Upper";
        public const string CoastVesselName = "Limbo Coaster";
        public const string EscapeVesselName = "Limbo Escaper";
        public const string RecordingGroup = "AutoMerge-Limbo";

        private const double BaseLat = -0.09;
        private const double BaseLon = -74.56;

        /// <summary>
        /// Populates <paramref name="writer"/> with the single pending-Limbo tree.
        /// Mirrors <c>RewindB9Fixture.PopulateWriter</c>'s shape so the injector body
        /// is a sibling of the existing ones.
        /// </summary>
        public static void PopulateWriter(ScenarioWriter writer, double baseUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            // ActiveRecordingId = the child, NOT null: that is what makes
            // TryRestoreActiveTreeNode pick `Limbo` over `LimboVesselSwitch`. Limbo is
            // the state the ordinary quickload-resume window parks a tree in, so it is
            // the one a player flow would actually be carrying.
            writer.AddRecordingsAsTree(
                BuildBuilders(baseUT),
                markerKey: TreeMarkerKey,
                activeRecordingId: ChildRecordingId);
        }

        /// <summary>
        /// The fixture's four recording builders, in emission order. Public so a test
        /// can materialize the SAME tree the injector writes
        /// (<c>ScenarioWriter.MaterializeTree(BuildBuilders(ut), ChildRecordingId)</c>)
        /// and machine-check the numbers the S0.9 / S0.10 specs predict, instead of
        /// those predictions resting on a source reading until a flight spends an hour
        /// disagreeing with them.
        /// </summary>
        public static RecordingBuilder[] BuildBuilders(double baseUT)
        {
            double splitUt = baseUT + 60.0;
            return new[]
            {
                BuildRoot(baseUT, splitUt),
                BuildChild(splitUt),
                BuildCoast(splitUt),
                BuildEscape(splitUt),
            };
        }

        // Pre-separation ascent (root). Climbs well past the 30 m idle-on-pad
        // threshold — not because a Limbo tree is subject to that discard (it is
        // gated on Finalized), but so a future Finalized-marker sibling of this
        // fixture measures the same subject rather than a degenerate one.
        private static RecordingBuilder BuildRoot(double baseUT, double splitUt)
        {
            var b = new RecordingBuilder(RootVesselName)
                .WithRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(RootRecordingId))
                .WithRecordingGroup(RecordingGroup);
            b.AddPoint(baseUT, BaseLat, BaseLon, 80);
            b.AddPoint(baseUT + 20, BaseLat, BaseLon, 4200);
            b.AddPoint(baseUT + 40, BaseLat, BaseLon, 18000);
            b.AddPoint(splitUt, BaseLat, BaseLon, 41000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket(RootVesselName, "Jebediah Kerman", pid: 310001)
                    .AsLanded(BaseLat, BaseLon, 80));
            return b;
        }

        // Surviving upper stage: Orbiting, i.e. the stable-terminal leaf whose
        // snapshot the full-fidelity branch preserves and the ghost-only branch nulls.
        private static RecordingBuilder BuildChild(double splitUt)
        {
            var b = new RecordingBuilder(ChildVesselName)
                .WithRecordingId(ChildRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(ChildRecordingId))
                .WithRecordingGroup(RecordingGroup)
                .WithTerminalState((int)TerminalState.Orbiting);
            b.AddPoint(splitUt, BaseLat, BaseLon, 41000);
            b.AddPoint(splitUt + 25, BaseLat, BaseLon, 66000);
            b.AddPoint(splitUt + 50, BaseLat, BaseLon, 82000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip(ChildVesselName, pid: 310002)
                    .AsOrbiting(700000, 0.01, 6.0, 0, 0, 0));
            return b;
        }

        // ------------------------------------------------------------------
        // THE TWO UN-FINALIZED LEAVES (added 2026-08-29). The two above were
        // authored before the fix and between them they do NOT exercise the
        // shape a REAL quickload stash has: `StashActiveTreeAsPendingLimbo`
        // runs no finalize pass and stamps NO terminal state, so the child's
        // `WithTerminalState(Orbiting)` is a finalized-shaped recording sitting
        // inside a Limbo tree. A flight over the old fixture therefore never
        // touched the null-terminal branch the fidelity fix newly depends on.
        // These two are that branch, one per outcome, so the flight's numbers
        // DISCRIMINATE rather than merely agree.
        // ------------------------------------------------------------------

        // Genuine Limbo shape, SPAWNABLE half: no terminal state, and a stable
        // ORBITING snapshot situation. The un-finalized gate maps ORBITING ->
        // Orbiting, which IsSpawnableTerminal admits, so this leaf's snapshot is
        // preserved AND stays spawn-at-end eligible - what a player who quickloaded
        // out of a parking orbit should get back.
        private static RecordingBuilder BuildCoast(double splitUt)
        {
            var b = new RecordingBuilder(CoastVesselName)
                .WithRecordingId(CoastRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(CoastRecordingId))
                .WithRecordingGroup(RecordingGroup);
            b.AddPoint(splitUt, BaseLat, BaseLon, 79000);
            b.AddPoint(splitUt + 30, BaseLat, BaseLon, 95000);
            b.AddPoint(splitUt + 60, BaseLat, BaseLon, 110000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip(CoastVesselName, pid: 310003)
                    .AsOrbiting(750000, 0.02, 3.0, 0, 0, 0));
            return b;
        }

        // Genuine Limbo shape, NON-SPAWNABLE half, and the one that measures the
        // 2026-08-29 un-finalized situation gate on a real flight: no terminal
        // state, snapshot situation ESCAPING. The finalize path maps ESCAPING ->
        // SubOrbital, which is NOT spawnable, so the dialog's decisions ghost-only
        // this leaf - its snapshot is released (GhostVisualSnapshot copied, crew
        // unreserved), not preserved. WITHOUT the gate it would read spawnable,
        // because the IsSpawnableTerminal rejection sits inside
        // `TerminalStateValue.HasValue` and the older situation check knows only
        // FLYING/SUB_ORBITAL. That is the whole difference the flight measures:
        // with the gate `snapshotsPreserved=3 snapshotsReleased=1`, without it
        // `snapshotsPreserved=4 snapshotsReleased=0`.
        private static RecordingBuilder BuildEscape(double splitUt)
        {
            var b = new RecordingBuilder(EscapeVesselName)
                .WithRecordingId(EscapeRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(EscapeRecordingId))
                .WithRecordingGroup(RecordingGroup);
            b.AddPoint(splitUt, BaseLat, BaseLon, 82000);
            b.AddPoint(splitUt + 40, BaseLat, BaseLon, 260000);
            b.AddPoint(splitUt + 80, BaseLat, BaseLon, 640000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip(EscapeVesselName, pid: 310004)
                    .AsOrbiting(900000, 0.03, 4.0, 0, 0, 0)
                    // AsOrbiting last, then override the situation: the orbital
                    // element block stays well-formed while `sit` carries the
                    // shape under test.
                    .WithSituation("ESCAPING"));
            return b;
        }
    }
}
