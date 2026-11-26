namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        // Forwarders to PartStateSeeder (Recording): the ghost builder's own names for
        // the snapshot reads a promotion shares.

        internal static SnapshotPartBaseline TryParseSnapshotPartBaseline(
            ConfigNode partNode,
            float? cargoBayClosedPosition = null,
            int cargoDeployModuleIndex = -1,
            bool cargoAllowDeployLimit = false,
            bool cargoRevClampPercent = false)
            => PartStateSeeder.TryParseSnapshotPartBaseline(
                partNode, cargoBayClosedPosition, cargoDeployModuleIndex,
                cargoAllowDeployLimit, cargoRevClampPercent);

        internal static ConfigNode FindSnapshotCargoAnimateGenericNode(
            ConfigNode partNode, int deployModuleIndex)
            => PartStateSeeder.FindSnapshotCargoAnimateGenericNode(partNode, deployModuleIndex);

        internal static float? ResolveSnapshotCargoBayDeployLimitStop(
            ConfigNode cargoAnimNode, bool allowDeployLimit, bool revClampPercent, float closedPosition)
            => PartStateSeeder.ResolveSnapshotCargoBayDeployLimitStop(
                cargoAnimNode, allowDeployLimit, revClampPercent, closedPosition);

        internal static bool TryResolveCargoBaySampleTimes(
            float closedPosition, float? deployLimitStop, out float closedTime, out float openTime)
            => PartStateSeeder.TryResolveCargoBaySampleTimes(
                closedPosition, deployLimitStop, out closedTime, out openTime);
    }
}
