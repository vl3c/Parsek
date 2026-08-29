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
    /// <para><b>What a run of it measures.</b> Booted at a NON-FLIGHT scene with
    /// <c>autoMerge</c> ON (which since the 2026-08-27 settings clamp is every
    /// player, always), the cold-load OnLoad's `pending-outside-flight` block should
    /// reach the auto-commit site with <c>pendingState=Limbo</c> and take the
    /// GHOST-ONLY branch, nulling both authored <c>VesselSnapshot</c>s. That is the
    /// flow the review panel could neither construct nor rule out. The fixture makes
    /// the question empirical instead of argumentative; it does NOT by itself claim
    /// a player produces this save.</para>
    ///
    /// <para><b>Both recordings carry a VesselSnapshot on purpose.</b>
    /// <c>snapshotsNulled=</c> in the ghost-only log line is the fidelity measurement,
    /// and a snapshot-less fixture would report 0 and prove nothing either way. The
    /// child is <c>Orbiting</c> — a stable terminal state — so it is exactly the leaf
    /// the full-fidelity branch WOULD have kept spawn-at-end eligible had the tree
    /// arrived Finalized. That contrast is the point.</para>
    /// </summary>
    public static class PendingLimboTreeFixture
    {
        public const string TreeMarkerKey = "isActive";

        public const string RootRecordingId = "limbo_root_2f7a41c8";
        public const string ChildRecordingId = "limbo_child_9b3e05d1";

        public const string RootVesselName = "Limbo Stack";
        public const string ChildVesselName = "Limbo Upper";
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

            double splitUt = baseUT + 60.0;

            // ActiveRecordingId = the child, NOT null: that is what makes
            // TryRestoreActiveTreeNode pick `Limbo` over `LimboVesselSwitch`. Limbo is
            // the state the ordinary quickload-resume window parks a tree in, so it is
            // the one a player flow would actually be carrying.
            writer.AddRecordingsAsTree(
                new[] { BuildRoot(baseUT, splitUt), BuildChild(splitUt) },
                markerKey: TreeMarkerKey,
                activeRecordingId: ChildRecordingId);
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
    }
}
