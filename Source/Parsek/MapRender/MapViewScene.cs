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
        // The flight-scene policy. Injected by the owning ParsekFlight right after it builds the policy
        // (SetPresenceDriver), the same way the controller pushes the per-frame inputs via
        // SetFrameInputs. Phase 8d.1: the CheckPendingMapVessels body relocated to
        // GhostMapPresence.UpdateFlightMapGhostLifecycle; the policy is still required here as the EXACT
        // source of the per-frame loop units (engine.CurrentLoopUnits via CurrentLoopUnitsForPresence)
        // that the relocated body needs.
        private ParsekPlaybackPolicy policy;

        public override bool IsActive => HighLogic.LoadedScene == GameScenes.FLIGHT;

        /// <summary>Inject the flight-scene presence driver (called once by ParsekFlight after it builds the policy).</summary>
        internal void SetPresenceDriver(ParsekPlaybackPolicy presenceDriver)
        {
            policy = presenceDriver;
        }

        // Phase 8d.1: drives the relocated flight-scene map-presence lifecycle directly on
        // GhostMapPresence, threading the policy's exact per-frame loop units (engine.CurrentLoopUnits
        // via CurrentLoopUnitsForPresence). Faithful relocation (no behavior change, no gate) of the
        // former policy.CheckPendingMapVessels(currentUT) call. The policy == null guard is preserved.
        public override void DriveMapPresence(double currentUT)
        {
            if (policy == null) return;
            GhostMapPresence.UpdateFlightMapGhostLifecycle(currentUT, policy.CurrentLoopUnitsForPresence);
        }
    }
}
