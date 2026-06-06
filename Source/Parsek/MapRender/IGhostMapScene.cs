using System.Collections.Generic;
using Parsek.Display;

namespace Parsek.MapRender
{
    /// <summary>
    /// The scene adapter (design §6.6, L4): the only thing implemented per scene. The
    /// Director/Sampler/Assembler/reconciler logic is written once against this interface and never
    /// knows which scene it is in. <see cref="MapViewScene"/> is the flight-map impl; a
    /// <c>TrackingStationScene</c> follows in Phase 7.
    ///
    /// <para>This is the Phase-4 (decision-only shadow) surface — the inputs the
    /// <see cref="ShadowRenderDriver"/> needs to compute intent per ghost: the active loop units (the
    /// <see cref="MissionLoopUnitBuilder.Build"/> output, NOT the engine's <c>CurrentLoopUnits</c>
    /// passthrough), the live UT, the set of map-ghost pids, the pid→trajectory resolve, and the body
    /// surface provider for the conic test. The DRAW-side members (position→scene projection,
    /// proto-vessel lifecycle pass-through, the shared per-frame floating-origin frame, camera-focus
    /// continuity across a swap — design §6.6) extend this interface in Phase 5, when the treatments
    /// actually draw; declaring them now, unused, would be speculative, so the interface grows with
    /// the phases.</para>
    /// </summary>
    internal interface IGhostMapScene
    {
        /// <summary>True when this scene is live and should run the pipeline (defensive scene gate).</summary>
        bool IsActive { get; }

        /// <summary>The live universal time for this frame.</summary>
        double CurrentUT { get; }

        /// <summary>The active loop units (the loop-unit owner's Build output, per design §6.7).</summary>
        GhostPlaybackLogic.LoopUnitSet LoopUnits { get; }

        /// <summary>The pids of the ghosts currently present on this scene's map.</summary>
        IReadOnlyCollection<uint> GhostPids { get; }

        /// <summary>
        /// Resolve a map-ghost pid to its playback trajectory and committed index. False when the pid
        /// is not a tracked ghost or its recording is gone.
        /// </summary>
        bool TryResolveGhost(uint pid, out IPlaybackTrajectory trajectory, out int committedIndex);

        /// <summary>
        /// Per-body surface geometry (radius) for the conic test (below-surface arcs → TracedPath).
        /// Wired to the live bodies in a scene impl; null-safe for the assembler.
        /// </summary>
        GhostTrajectoryPolylineRenderer.BodySurfaceProvider BodySurface { get; }

        /// <summary>
        /// True when <paramref name="bodyName"/> is a star (the Sun). Used to detect a member's
        /// heliocentric leg: in a re-aimed mission only the heliocentric (Sun-relative) member is
        /// re-synthesized; the Kerbin-departure and destination-arrival members are faithful and DO
        /// render (design §4), so the shadow skips only the star-relative member, not the whole
        /// mission. Resolved against the live bodies; false for an unknown body.
        /// </summary>
        bool IsStarBody(string bodyName);

        // ---- Draw-side (Phase 5): the surface handles the treatments drive ----

        /// <summary>
        /// The live ghost proto-vessel's <see cref="Orbit"/> for <paramref name="pid"/> (the object the
        /// stock OrbitRenderer draws the line from and the icon rides). <see cref="StockConicTreatment"/>
        /// seeds and drives THIS one orbit so the icon and line come from a single source. False when
        /// the pid has no live proto-vessel / orbit driver.
        /// </summary>
        bool TryGetGhostOrbit(uint pid, out Orbit orbit);

        /// <summary>Resolve a body by name against the live bodies (null when unknown).</summary>
        CelestialBody ResolveBody(string bodyName);

        /// <summary>
        /// Drive this scene's per-frame ghost map-presence (proto-vessel) lifecycle at
        /// <paramref name="currentUT"/>. The FLIGHT scene delegates to the pending-queue model
        /// (<c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>); the Tracking Station drives
        /// <c>GhostMapPresence.UpdateTrackingStationGhostLifecycle</c>. This is the seam (Phase 8d.0)
        /// that routes the host scene's presence tick through the scene adapter so the body can be
        /// relocated behind the adapter later (Phase 8d.1); the override is byte-identical to the
        /// direct host call today.
        /// </summary>
        void DriveMapPresence(double currentUT);
    }
}
