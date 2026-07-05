using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Tracking-Station implementation of <see cref="IGhostMapScene"/> (design §6.6, Phase 7a). Same
    /// scene-agnostic plumbing as <see cref="MapViewScene"/> (via <see cref="GhostMapSceneBase"/>); only
    /// the scene gate differs (TRACKSTATION). The owning <see cref="ParsekTrackingStation"/> pushes the
    /// per-frame loop units + UT and runs <see cref="ShadowRenderDriver"/> against it.
    ///
    /// <para>Phase 7a origin note, updated post-Phase-8: the driver run over the TS map ghosts now stamps
    /// the live drive (the StockConic seed + traced-path stamps), no longer a decision-only shadow, and
    /// the reconciler's decision-vs-old-truth comparator was RETIRED in the Phase-8 unwiring (the
    /// recorded-vs-rendered <c>RenderParityOracle</c> is the sole acceptance axis). TS renders a single
    /// span instance (no overlap machinery), which the span clock's
    /// <c>ResolveTrackingStationSampleUT</c> remap already enforces.</para>
    /// </summary>
    internal sealed class TrackingStationScene : GhostMapSceneBase
    {
        public override bool IsActive => HighLogic.LoadedScene == GameScenes.TRACKSTATION;

        // TS presence-drive seam for symmetry with MapViewScene (Phase 8d.0). Delegates to the TS
        // lifecycle pass, sampling the same per-frame loop units the base already carries
        // (SetFrameInputs). NOT yet routed from ParsekTrackingStation — its existing direct
        // UpdateTrackingStationGhostLifecycle(cachedLoopUnits) calls are untouched in 8d.0; routing
        // the TS host through this seam is a later phase. Byte-identical to that direct call.
        public override void DriveMapPresence(double currentUT)
        {
            GhostMapPresence.UpdateTrackingStationGhostLifecycle(LoopUnits);
        }
    }
}
