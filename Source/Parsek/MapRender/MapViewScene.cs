using System.Collections.Generic;
using Parsek.Display;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Flight-map implementation of <see cref="IGhostMapScene"/> (design §6.6). Phase 4 decision-only
    /// shadow scope: it surfaces the inputs the <see cref="ShadowRenderDriver"/> needs and is a thin
    /// pass-through to the existing map-presence state (<see cref="GhostMapPresence"/>) + the live
    /// bodies (<see cref="FlightGlobals"/>). It writes NOTHING — the shadow only computes intent and
    /// reconciles it against the old path's truth.
    ///
    /// <para>The owning <see cref="ParsekFlight"/> sets the per-frame inputs via
    /// <see cref="SetFrameInputs"/> right after it (re)builds the loop units, so this scene consumes
    /// the loop-unit owner's Build output directly (design §6.7), not the engine passthrough. The
    /// draw-side members (projection, proto lifecycle, floating-origin frame, focus) land here in
    /// Phase 5 as pass-throughs to <see cref="GhostMapPresence"/>, which is what makes the Phase-8
    /// cutover a flip rather than a relocation.</para>
    /// </summary>
    internal sealed class MapViewScene : IGhostMapScene
    {
        private GhostPlaybackLogic.LoopUnitSet loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
        private double currentUT;

        /// <summary>Set this frame's loop units + UT (called by ParsekFlight before RunFrame).</summary>
        internal void SetFrameInputs(GhostPlaybackLogic.LoopUnitSet units, double ut)
        {
            loopUnits = units ?? GhostPlaybackLogic.LoopUnitSet.Empty;
            currentUT = ut;
        }

        public bool IsActive => HighLogic.LoadedScene == GameScenes.FLIGHT;

        public double CurrentUT => currentUT;

        public GhostPlaybackLogic.LoopUnitSet LoopUnits => loopUnits;

        public IReadOnlyCollection<uint> GhostPids => GhostMapPresence.ghostMapVesselPids;

        // Delegates to GhostMapPresence (which owns the pid->recording maps and the raw committed read)
        // so the ERS-exempt physical-correlation read stays in one allowlisted place.
        public bool TryResolveGhost(uint pid, out IPlaybackTrajectory trajectory, out int committedIndex)
            => GhostMapPresence.TryGetCommittedTrajectoryForPid(pid, out trajectory, out committedIndex);

        public GhostTrajectoryPolylineRenderer.BodySurfaceProvider BodySurface => ResolveBodySurface;

        // FlightGlobals-backed body radius lookup (mirrors the polyline renderer's Driver provider).
        // Isolated so the pure pipeline never calls FlightGlobals; the assembler accepts null.
        private static bool ResolveBodySurface(
            string bodyName, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info)
        {
            info = default(GhostTrajectoryPolylineRenderer.BodySurfaceInfo);
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                return false;
            info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo { radius = body.Radius };
            return true;
        }
    }
}
