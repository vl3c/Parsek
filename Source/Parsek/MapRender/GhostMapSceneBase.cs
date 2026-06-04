using System.Collections.Generic;
using Parsek.Display;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Shared <see cref="IGhostMapScene"/> implementation for the map-capable scenes. Everything except
    /// the scene gate (<see cref="IsActive"/>) is scene-agnostic: the pid -> recording resolve, the
    /// live-body lookups, and the proto-vessel orbit handle all go through <see cref="GhostMapPresence"/>
    /// + <see cref="FlightGlobals"/>, which behave identically in FLIGHT and the Tracking Station. The
    /// owning controller pushes the per-frame loop units + UT via <see cref="SetFrameInputs"/> right
    /// after it (re)builds them, so each scene consumes the loop-unit owner's Build output directly
    /// (design §6.7). <see cref="MapViewScene"/> and <see cref="TrackingStationScene"/> only override the
    /// scene gate.
    /// </summary>
    internal abstract class GhostMapSceneBase : IGhostMapScene
    {
        private GhostPlaybackLogic.LoopUnitSet loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
        private double currentUT;

        /// <summary>Set this frame's loop units + UT (called by the controller before RunFrame).</summary>
        internal void SetFrameInputs(GhostPlaybackLogic.LoopUnitSet units, double ut)
        {
            loopUnits = units ?? GhostPlaybackLogic.LoopUnitSet.Empty;
            currentUT = ut;
        }

        public abstract bool IsActive { get; }

        public double CurrentUT => currentUT;

        public GhostPlaybackLogic.LoopUnitSet LoopUnits => loopUnits;

        public IReadOnlyCollection<uint> GhostPids => GhostMapPresence.ghostMapVesselPids;

        // Delegates to GhostMapPresence (which owns the pid->recording maps + the ERS-exempt committed
        // read) so the physical-correlation read stays in one allowlisted place.
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

        public bool IsStarBody(string bodyName)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            return body != null && body.isStar;
        }

        public bool TryGetGhostOrbit(uint pid, out Orbit orbit)
        {
            orbit = null;
            if (!FlightGlobals.FindVessel(pid, out Vessel v) || v == null || v.orbitDriver == null)
                return false;
            orbit = v.orbitDriver.orbit;
            return orbit != null;
        }

        public CelestialBody ResolveBody(string bodyName) => FlightGlobals.GetBodyByName(bodyName);
    }
}
