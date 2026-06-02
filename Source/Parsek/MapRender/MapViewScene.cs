using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Flight-map implementation of <see cref="IGhostMapScene"/> (design §6.6). All the resolve / body
    /// / orbit plumbing lives in <see cref="GhostMapSceneBase"/> (scene-agnostic); this only pins the
    /// scene gate to FLIGHT. The owning <see cref="ParsekFlight"/> pushes the per-frame loop units + UT
    /// via <see cref="GhostMapSceneBase.SetFrameInputs"/> and runs <see cref="ShadowRenderDriver"/>
    /// against it; nothing here writes to the stock surfaces (decision-only shadow).
    /// </summary>
    internal sealed class MapViewScene : GhostMapSceneBase
    {
        public override bool IsActive => HighLogic.LoadedScene == GameScenes.FLIGHT;
    }
}
