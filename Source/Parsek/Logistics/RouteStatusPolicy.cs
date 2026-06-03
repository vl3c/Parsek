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
    /// Three orthogonal predicates over all 9 <see cref="RouteStatus"/> values:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="BindsTree"/>: TRUE for every status. A route binds
    ///   its tree (blocking manual looping on that tree) whenever it exists and
    ///   has not been explicitly removed. Broken routes
    ///   (<see cref="RouteStatus.MissingSourceRecording"/>,
    ///   <see cref="RouteStatus.SourceChanged"/>,
    ///   <see cref="RouteStatus.EndpointLost"/>) keep owning the tree until the
    ///   player removes them, which prevents a self-heal double-owner
    ///   collision (confirmed decision).</item>
    ///   <item><see cref="GhostDriving"/>: TRUE only when the route is live
    ///   enough to render a looping ghost: Active, InTransit,
    ///   WaitingForResources, WaitingForFunds, DestinationFull. Paused and the
    ///   three broken states render nothing.</item>
    ///   <item><see cref="Broken"/>: TRUE for the three hard-broken states
    ///   (EndpointLost, MissingSourceRecording, SourceChanged) that need player
    ///   action. This is the single source of truth for "is this route broken",
    ///   consumed by the main-window Logistics button red tint.</item>
    /// </list>
    /// <para>
    /// The switch statements are exhaustive and the <c>default</c> arm throws,
    /// so appending a new <see cref="RouteStatus"/> value fails loudly until a
    /// human classifies it in ALL three predicates.
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
                        + "classify it in ALL three predicates (BindsTree, GhostDriving, Broken) before shipping.");
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
                        + "classify it in ALL three predicates (BindsTree, GhostDriving, Broken) before shipping.");
            }
        }

        /// <summary>
        /// TRUE for the three hard-broken route states that need explicit player
        /// action (the destination vessel was lost, the source recording is
        /// missing, or the source recording changed under the route): EndpointLost,
        /// MissingSourceRecording, SourceChanged. FALSE for every live or paused
        /// status. Single source of truth for the main-window Logistics button red
        /// tint, so the button and this policy can never disagree on what "broken"
        /// means.
        /// </summary>
        internal static bool Broken(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.EndpointLost:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return true;
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                case RouteStatus.Paused:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status),
                        status,
                        "RouteStatusPolicy.Broken: unclassified RouteStatus value; "
                        + "classify it in ALL three predicates (BindsTree, GhostDriving, Broken) before shipping.");
            }
        }
    }
}
