using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 6 ε resolver seam (design doc §7.4 / §7.5 / §7.6 / §7.10 /
    /// §18 Phase 6). The propagator delegates the four world-frame
    /// "high-fidelity reference position at this UT" lookups through this
    /// interface, then computes
    /// <c>ε = referenceWorldPos − P_smoothed_world(UT)</c>. Splitting these
    /// into one interface keeps the production wiring (which needs live
    /// <see cref="FlightGlobals"/> access) on one side and the xUnit double
    /// (which fakes deterministic poses) on the other.
    ///
    /// <para>
    /// All four methods follow the same contract: return
    /// <see langword="true"/> + a finite world-frame
    /// <see cref="Vector3d"/> on success; return <see langword="false"/>
    /// when the resolver cannot satisfy the lookup (missing live anchor,
    /// missing checkpoint, missing absolute shadow, etc.). The propagator
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
        /// recorded <c>absoluteFrames</c> shadow when the live anchor is
        /// unreliable). The candidate's <paramref name="sectionIndex"/>
        /// always points at the ABSOLUTE side of the boundary by
        /// AnchorCandidateBuilder construction; the resolver finds the
        /// adjacent RELATIVE section internally.
        /// </summary>
        bool TryResolveRelativeBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos);

        /// <summary>
        /// §7.5 OrbitalCheckpoint world-frame reference. Resolves the
        /// Kepler position at <paramref name="boundaryUT"/> using the
        /// <see cref="OrbitSegment"/> on the segment-side opposite the
        /// boundary (i.e., the OrbitalCheckpoint section's
        /// <c>checkpoints</c> list).
        /// </summary>
        bool TryResolveOrbitalCheckpointWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos);

        /// <summary>
        /// §7.6 SOI transition world-frame reference. Same as §7.5 but the
        /// checkpoint stores Keplerian elements in the post-SOI body's
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
