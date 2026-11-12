using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 6 eps resolver seam (design doc section 7.4 / section 7.6 / section 7.10 /
    /// section 18 Phase 6). The propagator delegates the three world-frame
    /// "high-fidelity reference position at this UT" lookups through this
    /// interface, then computes
    /// <c>ε = referenceWorldPos − P_smoothed_world(UT)</c>. Splitting these
    /// into one interface keeps the production wiring (which needs live
    /// <see cref="FlightGlobals"/> access) on one side and the xUnit double
    /// (which fakes deterministic poses) on the other.
    ///
    /// <para>
    /// OrbitalCheckpoint (section 7.5) and BubbleEntry / BubbleExit (section 7.7) take no
    /// resolver: both of their sides are recorded, so the propagator compares
    /// them in the body-fixed frame of the boundary instant
    /// (<see cref="AnchorPropagator.TryResolveBoundaryInstantEpsilon"/>).
    /// </para>
    ///
    /// <para>
    /// All three methods follow the same contract: return
    /// <see langword="true"/> + a finite world-frame
    /// <see cref="Vector3d"/> on success; return <see langword="false"/>
    /// when the resolver cannot satisfy the lookup (missing live anchor,
    /// missing checkpoint, missing body-fixed primary, etc.). The propagator
    /// emits a <c>Pipeline-Anchor</c> Verbose line on failure and leaves
    /// ε = 0 for that slot — the §7.11 priority slot is still reserved
    /// (HR-9: visible failure surface, not silent zero).
    /// </para>
    /// </summary>
    internal interface IAnchorWorldFrameResolver
    {
        /// <summary>
        /// §7.4 RELATIVE-boundary world-frame reference. The <paramref name="boundaryUT"/>
        /// is the seam UT shared by the ABSOLUTE-and-RELATIVE adjacent
        /// sections; the resolver returns the world position computed via
        /// the appropriate version-dispatch path
        /// (<see cref="TrajectoryMath.ResolveRelativePlaybackPosition"/>
        /// for v6+; legacy world-offset for v5; v7+ may consult the
        /// recorded <c>bodyFixedFrames</c> shadow when the live anchor is
        /// unreliable). The candidate's <paramref name="sectionIndex"/>
        /// always points at the ABSOLUTE side of the boundary by
        /// AnchorCandidateBuilder construction; the resolver finds the
        /// adjacent RELATIVE section internally.
        /// </summary>
        bool TryResolveRelativeBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos);

        /// <summary>
        /// section 7.6 SOI transition world-frame reference: the Kepler position of the
        /// neighbouring checkpoint section at the boundary UT. The checkpoint stores Keplerian elements in the post-SOI body's
        /// frame; the resolver constructs the <see cref="Orbit"/> against
        /// that body and propagates to the boundary UT.
        /// </summary>
        bool TryResolveSoiBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos);

        /// <summary>
        /// §7.10 Loop world-frame reference. Returns the loop anchor
        /// vessel's current world position (production: live
        /// <see cref="Vessel.GetWorldPos3D"/>; tests: fixed pose). Phase 6
        /// uses the anchor's pose at session entry as the loop seed —
        /// per-cycle phase math is owned by the loop-playback path.
        /// </summary>
        bool TryResolveLoopAnchorWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double sampleUT, out Vector3d worldPos);
    }
}
