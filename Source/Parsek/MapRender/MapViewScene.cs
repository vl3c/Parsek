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
        // The flight-scene presence driver. Injected by the owning ParsekFlight right after it builds
        // the policy (SetPresenceDriver), the same way the controller pushes the per-frame inputs via
        // SetFrameInputs. DriveMapPresence delegates here verbatim — the Phase 8d.0 seam is a pure
        // indirection; relocating the CheckPendingMapVessels body behind the adapter is Phase 8d.1.
        private ParsekPlaybackPolicy policy;

        public override bool IsActive => HighLogic.LoadedScene == GameScenes.FLIGHT;

        /// <summary>Inject the flight-scene presence driver (called once by ParsekFlight after it builds the policy).</summary>
        internal void SetPresenceDriver(ParsekPlaybackPolicy presenceDriver)
        {
            policy = presenceDriver;
        }

        // Byte-identical to the former direct ParsekFlight call site: same method, same argument, same
        // execution-order slot, same surrounding try-catch. This is a pure pass-through (Phase 8d.0).
        public override void DriveMapPresence(double currentUT)
        {
            policy?.CheckPendingMapVessels(currentUT);
        }
    }
}
