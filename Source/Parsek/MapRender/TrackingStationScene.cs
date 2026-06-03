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
    }
}
