using System;

namespace Parsek
{
    /// <summary>
    /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9): per-<see cref="TrajectoryPoint"/>
    /// flag bitset attached at <see cref="RecordingStore.StructuralEventFlagFormatVersion"/>
    /// (v10) and later. The byte rides at the very end of every TrajectoryPoint's binary
    /// record so legacy v9-or-earlier readers stop short of it harmlessly (they never read
    /// the byte and default-init <see cref="TrajectoryPoint.flags"/> to <c>0</c>).
    ///
    /// <para>
    /// Bit 0 (<see cref="StructuralEventSnapshot"/>) marks a synthetic snapshot the
    /// recorder appended at the exact UT of a structural event (dock / undock / EVA /
    /// joint-break) so anchor ε at re-fly merge points lands at physics-precision
    /// instead of a one-tick interpolation. Bits 1-7 are reserved for future per-sample
    /// metadata; new bits must be additive (bit 0 must keep its meaning so v10 round-trip
    /// stays stable across phases).
    /// </para>
    /// </summary>
    [Flags]
    internal enum TrajectoryPointFlags : byte
    {
        /// <summary>No flags set — the default for every legacy point and every
        /// regular per-tick sample post-v10.</summary>
        None = 0,

        /// <summary>The recorder appended this point at the exact UT of a structural
        /// event (Section 12). Anchor ε resolution prefers this sample over any
        /// interpolated neighbour.</summary>
        StructuralEventSnapshot = 1 << 0,

        // Bits 1-7 reserved. When adding a new bit, keep StructuralEventSnapshot at
        // bit 0 — the binary codec round-trips the raw byte, but readers / writers
        // older than the new bit's gating version must still see a valid bit-0 value.
    }
}
