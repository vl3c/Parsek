using System;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// The surviving half of what used to be the post-commit "Create Supply Route?" modal:
    /// one pure geometry helper, <see cref="ComputeRootToUndockSpan"/>, which several route
    /// surfaces share as their default-interval derivation.
    /// <para>The dialog itself was deleted 2026-09-11 (GUI census D4). Its docstring said
    /// "Fired by MergeDialog.OnTreeCommitted", a hook that no longer exists: `TryShow`,
    /// `TryShowDeferredIfPending`, `Spawn`, `OnConfirm` and `OnCancel` had test callers
    /// only, so no player could reach the modal, and `DismissIfOpen` - the one live
    /// production call, from the flight scene's scene-change cleanup - could therefore only
    /// ever no-op. The player-facing route-creation path is the Logistics window's
    /// Candidates section plus the main window's Record-Supply-Run banner
    /// (<c>RouteRunPrompt</c>), neither of which went through here.</para>
    /// <para>The class NAME is kept for call-site stability: five production sites and one
    /// harness command path call <c>RouteCreationDialog.ComputeRootToUndockSpan</c>, and
    /// several of their comments name it as the shared-geometry reference.</para>
    /// </summary>
    internal static class RouteCreationDialog
    {
        /// <summary>
        /// The rendered <c>[root..undock]</c> span (<c>undockUT - rootLaunchUT</c>,
        /// i.e. the <c>N=1</c> dispatch cadence floor) used as the default route interval.
        /// The root launch UT comes from the tree ROOT recording's <c>StartUT</c> (the
        /// launch), NOT <c>source.StartUT</c> (the mid-flight dock child) - mirrors
        /// <see cref="RouteBuilder.BuildRoute"/>'s geometry so a default can never trip the
        /// builder's <c>interval-below-transit</c> reject. Falls back to the leaf span
        /// (then 1.0) when the root / undock UT cannot be resolved; floored at 1.0.
        /// </summary>
        internal static double ComputeRootToUndockSpan(RouteAnalysisResult result, RecordingTree tree)
        {
            // NOTE (playtest follow-up): the rendered route segment now ends at the
            // DOCK, not the undock (rendering stops at the docking moment; the docked
            // combined vessel is not rendered). So this returns the [root .. DOCK]
            // span = the route's TransitDuration. The method name is retained for
            // call-site stability; "Undock" is historical.
            Recording source = result?.SourceRecording;
            double leafSpan = source != null
                ? Math.Max(1.0, source.EndUT - source.StartUT)
                : 1.0;

            double dockUT = result?.ConnectionWindow != null
                ? result.ConnectionWindow.DockUT
                : double.NaN;
            if (double.IsNaN(dockUT) || double.IsInfinity(dockUT))
                return leafSpan;

            // Root launch UT from the tree ROOT (the launch site), falling back to
            // the source recording's StartUT when the tree has no resolvable root.
            double rootLaunchUT = source != null ? source.StartUT : double.NaN;
            if (tree?.Recordings != null
                && !string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out Recording rootRec)
                && rootRec != null)
            {
                rootLaunchUT = rootRec.StartUT;
            }
            // (M-MIS-5 P2b) A mid-tree docked-origin run spans from the ORIGIN
            // UNDOCK, not the tree root - mirror RouteBuilder's span start so the
            // default equals the built route's TransitDuration (N=1).
            if (result != null && result.IsMidTreeDockedOrigin
                && result.OriginConnectionWindow != null)
            {
                rootLaunchUT = result.OriginConnectionWindow.UndockUT;
            }
            if (double.IsNaN(rootLaunchUT) || double.IsInfinity(rootLaunchUT))
                return leafSpan;

            double span = dockUT - rootLaunchUT;
            return span > 0.0 ? span : leafSpan;
        }
    }
}
