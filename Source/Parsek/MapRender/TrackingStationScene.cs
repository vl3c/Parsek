using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Tracking-Station implementation of <see cref="IGhostMapScene"/> (design §6.6, Phase 7a). Same
    /// scene-agnostic plumbing as <see cref="MapViewScene"/> (via <see cref="GhostMapSceneBase"/>); only
    /// the scene gate differs (TRACKSTATION). The owning <see cref="ParsekTrackingStation"/> pushes the
    /// per-frame loop units + UT and runs <see cref="ShadowRenderDriver"/> against it.
    ///
    /// <para>Phase 7a is shadow PARITY: the pipeline runs over the TS map ghosts in decision-only shadow
    /// (writes nothing), so the reconciler reports decision-vs-old-truth against today's
    /// <c>ResolveTrackingStationSampleUT</c> remap. TS renders a single span instance (no overlap
    /// machinery), which the span clock already enforces; the new-behaviour make-before-break /
    /// cold-start handling is Phase 7b, gated on the §15.2 in-game probe.</para>
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
