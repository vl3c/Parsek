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
            CelestialBody body = ResolveBodyByNameSafe(bodyName);
            if (body == null)
                return false;
            info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo { radius = body.Radius };
            return true;
        }

        public bool IsStarBody(string bodyName)
        {
            CelestialBody body = ResolveBodyByNameSafe(bodyName);
            return body != null && body.isStar;
        }

        // FlightGlobals.GetBodyByName resolves through a Dictionary<string,CelestialBody>
        // (fetch.bodyNames) and calls ContainsKey(name) with NO null guard (decompiled KSP 1.12.5),
        // so a null/empty body name throws ArgumentNullException("key"). A null body name is normal on
        // this path - GhostRenderIntent.Hidden carries FrameBodyName=null, and an OrbitSegment can lack
        // a body - so guard before the lookup (mirroring the autonomous polyline Driver's
        // ResolveBodySurface/ResolveBodyByName string.IsNullOrEmpty guard) and treat null/empty as
        // "no body": the not-found result every caller already handles. Without this the default-on
        // director-drive shadow (ShadowRenderDriver.RunFrame) throws and is suppressed per frame,
        // dropping the icon drive to the legacy path for that frame.
        private static CelestialBody ResolveBodyByNameSafe(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
            {
                ParsekLog.VerboseRateLimited("MapRender", "scene-null-body-name",
                    "GhostMapScene body lookup skipped: null/empty body name (treated as no body)", 10.0);
                return null;
            }
            return FlightGlobals.GetBodyByName(bodyName);
        }

        public bool TryGetGhostOrbit(uint pid, out Orbit orbit)
        {
            orbit = null;
            if (!FlightGlobals.FindVessel(pid, out Vessel v) || v == null || v.orbitDriver == null)
                return false;
            orbit = v.orbitDriver.orbit;
            return orbit != null;
        }

        public CelestialBody ResolveBody(string bodyName) => ResolveBodyByNameSafe(bodyName);

        // Presence-drive differs per scene: FLIGHT runs the pending-queue model
        // (ParsekPlaybackPolicy.CheckPendingMapVessels); the Tracking Station runs
        // GhostMapPresence.UpdateTrackingStationGhostLifecycle. Each scene supplies its own body.
        public abstract void DriveMapPresence(double currentUT);
    }
}
