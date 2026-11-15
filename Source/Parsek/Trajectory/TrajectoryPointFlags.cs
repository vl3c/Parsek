using System;

namespace Parsek
{
    /// <summary>
    /// Per-<see cref="TrajectoryPoint"/> flag bitset. The byte rides at the
    /// end of every TrajectoryPoint binary record in the current v0 layout.
    ///
    /// <para>
    /// Bit 0 (<see cref="StructuralEventSnapshot"/>) marks a synthetic snapshot the
    /// recorder appended at the exact UT of a structural event (dock / undock / EVA /
    /// joint-break) so anchor ε at re-fly merge points lands at physics-precision
    /// instead of a one-tick interpolation. Bits 1-7 are reserved for future per-sample
    /// metadata; new bits must be additive and preserve bit 0's meaning.
    /// </para>
    /// </summary>
    [Flags]
    internal enum TrajectoryPointFlags : byte
    {
        /// <summary>No flags set — the default for every regular per-tick sample.</summary>
        None = 0,

        /// <summary>The recorder appended this point at the exact UT of a structural
        /// event (Section 12). Anchor ε resolution prefers this sample over any
        /// interpolated neighbour.</summary>
        StructuralEventSnapshot = 1 << 0,

        // Bits 1-7 reserved. When adding a new bit, keep StructuralEventSnapshot at bit 0.
    }
}
