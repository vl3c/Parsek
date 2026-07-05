using System.Globalization;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §10: a typed sphere-of-influence boundary crossing, unifying the ≥4 places a
    /// body change is re-derived today (<c>GhostOrbitBodyChanged</c>, <c>MapRenderProbe.bodyChanged</c>,
    /// the <c>lastLineToggleBody</c> blink guard, <c>SeamBetween</c>'s string compare).
    ///
    /// <para><b>NEW, additive, NOT wired in Phase 1. v1 RECORDS, does not ENFORCE</b> — the
    /// <see cref="ExitState"/>/<see cref="EntryState"/> are carried for future continuity work; the
    /// cross-SOI ~62° kink fix (the whole-patched-conic-chain synthesis) is deferred (design §9.2 /
    /// §17). A <see cref="PhaseSeam"/> references this only at a body change.</para>
    /// </summary>
    internal sealed class SoiCrossing
    {
        /// <summary>The body left at the crossing.</summary>
        internal string FromBody { get; }
        /// <summary>The body entered at the crossing.</summary>
        internal string ToBody { get; }
        /// <summary>The assembled-chain UT of the crossing.</summary>
        internal double CrossingUt { get; }
        /// <summary>The SOI radius (metres) at the crossing.</summary>
        internal double SoiRadius { get; }
        /// <summary>State vector leaving <see cref="FromBody"/>'s SOI (for future continuity; v1 unused).</summary>
        internal Vector3d ExitState { get; }
        /// <summary>State vector entering <see cref="ToBody"/>'s SOI (for future continuity; v1 unused).</summary>
        internal Vector3d EntryState { get; }

        internal SoiCrossing(
            string fromBody, string toBody, double crossingUt, double soiRadius,
            Vector3d exitState = default(Vector3d), Vector3d entryState = default(Vector3d))
        {
            FromBody = fromBody;
            ToBody = toBody;
            CrossingUt = crossingUt;
            SoiRadius = soiRadius;
            ExitState = exitState;
            EntryState = entryState;
        }

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "SOI {0}->{1} UT={2:F1} r={3:F0}",
                FromBody ?? "?", ToBody ?? "?", CrossingUt, SoiRadius);
    }
}
