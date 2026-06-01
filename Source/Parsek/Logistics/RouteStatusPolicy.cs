using System;

namespace Parsek.Logistics
{
    /// <summary>
    /// Single shared status-policy table for Supply Routes. Both the
    /// mutual-exclusion guard (does this route still own/bind its tree?) and
    /// the ghost-driving selector (should this route render its loop right
    /// now?) call this ONE file, so the two never diverge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plan: <c>docs/dev/plan-logistics-routes-on-missions.md</c> Phase 0 task 1.
    /// Design: <c>docs/parsek-logistics-supply-routes-design.md</c> §0.5/§0.6.
    /// </para>
    /// <para>
    /// Two orthogonal predicates over all 9 <see cref="RouteStatus"/> values:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="BindsTree"/> — TRUE for every status. A route binds
    ///   its tree (blocking manual looping on that tree) whenever it exists and
    ///   has not been explicitly removed. Broken routes
    ///   (<see cref="RouteStatus.MissingSourceRecording"/>,
    ///   <see cref="RouteStatus.SourceChanged"/>,
    ///   <see cref="RouteStatus.EndpointLost"/>) keep owning the tree until the
    ///   player removes them, which prevents a self-heal double-owner
    ///   collision (confirmed decision).</item>
    ///   <item><see cref="GhostDriving"/> — TRUE only when the route is live
    ///   enough to render a looping ghost: Active, InTransit,
    ///   WaitingForResources, WaitingForFunds, DestinationFull. Paused and the
    ///   three broken states render nothing.</item>
    /// </list>
    /// <para>
    /// The switch statements are exhaustive and the <c>default</c> arm throws,
    /// so appending a new <see cref="RouteStatus"/> value fails loudly until a
    /// human classifies it in BOTH predicates.
    /// </para>
    /// </remarks>
    internal static class RouteStatusPolicy
    {
        /// <summary>
        /// TRUE when a route in this status BINDS its tree, blocking manual
        /// looping (mission-window and recordings-window) on that tree. TRUE
        /// for all 9 statuses: a route binds its tree until explicitly removed.
        /// </summary>
        internal static bool BindsTree(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                case RouteStatus.EndpointLost:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                case RouteStatus.Paused:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status),
                        status,
                        "RouteStatusPolicy.BindsTree: unclassified RouteStatus value; "
                        + "classify it in BOTH BindsTree and GhostDriving before shipping.");
            }
        }

        /// <summary>
        /// TRUE when a route in this status should render its looping ghost.
        /// TRUE for Active, InTransit, WaitingForResources, WaitingForFunds,
        /// DestinationFull; FALSE for Paused, EndpointLost,
        /// MissingSourceRecording, SourceChanged.
        /// </summary>
        internal static bool GhostDriving(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                    return true;
                case RouteStatus.Paused:
                case RouteStatus.EndpointLost:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status),
                        status,
                        "RouteStatusPolicy.GhostDriving: unclassified RouteStatus value; "
                        + "classify it in BOTH BindsTree and GhostDriving before shipping.");
            }
        }
    }
}
