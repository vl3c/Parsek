using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §6.1: the join between two adjacent <see cref="TrajectoryPhase"/>s, carrying a
    /// continuity contract. The successor of the cosmetic 2-value <c>RenderSegment.SeamKind</c>.
    ///
    /// <para><b>NEW enum, NOT a mutation of the live <see cref="SeamKind"/>.</b> The live draw path
    /// switches on <c>RenderSegment.SeamKind</c> (a 2-value <c>None/Rigid/FlexibleSoi</c>); this Phase-1
    /// type needs a third value (<see cref="PhaseSeamKind.SwitchContinuation"/>) plus a continuity
    /// order, and is additive / unwired, so it is a SEPARATE enum
    /// (<see cref="PhaseSeamKind"/> / <see cref="ContinuityOrder"/>) — the live enum is untouched so no
    /// existing switch breaks. Old → new mapping: <c>SeamKind.None</c> has no <see cref="PhaseSeam"/>
    /// (null), <c>SeamKind.Rigid</c> → <see cref="PhaseSeamKind.Rigid"/>, <c>SeamKind.FlexibleSoi</c> →
    /// <see cref="PhaseSeamKind.FlexibleSoi"/>.</para>
    /// </summary>
    internal sealed class PhaseSeam
    {
        /// <summary>The seam's kind (design §6.1).</summary>
        internal PhaseSeamKind Kind { get; }
        /// <summary>The continuity order the seam asserts (G0 position, G1 position + tangent).</summary>
        internal ContinuityOrder Continuity { get; }
        /// <summary>Non-null at a body change (design §10); null otherwise.</summary>
        internal SoiCrossing Crossing { get; }
        /// <summary>Does the seam ever render in view? (off-camera seams tolerate discontinuity).</summary>
        internal bool OnCamera { get; }

        internal PhaseSeam(
            PhaseSeamKind kind,
            ContinuityOrder continuity,
            SoiCrossing crossing = null,
            bool onCamera = false)
        {
            Kind = kind;
            Continuity = continuity;
            Crossing = crossing;
            OnCamera = onCamera;
        }

        /// <summary>
        /// design §6.1: the canonical <b>Rigid + G1</b> seam — ascent↔orbit and orbit↔landing (descent
        /// re-stitch). A tangent mismatch beyond tolerance raises <c>rigid-seam-tangent-discontinuity</c>
        /// (the predicate is <see cref="PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity"/>).
        /// </summary>
        internal static PhaseSeam Rigid(bool onCamera = true)
            => new PhaseSeam(PhaseSeamKind.Rigid, ContinuityOrder.G1, crossing: null, onCamera: onCamera);

        /// <summary>
        /// design §6.1: the <b>FlexibleSoi + G0</b> seam — the two recorded↔synthetic SOI boundaries
        /// (the ~62° kink lives here; tolerated in v1). Carries the <see cref="SoiCrossing"/>.
        /// </summary>
        internal static PhaseSeam FlexibleSoi(SoiCrossing crossing, bool onCamera = false)
            => new PhaseSeam(PhaseSeamKind.FlexibleSoi, ContinuityOrder.G0, crossing: crossing, onCamera: onCamera);

        /// <summary>
        /// design §6.1: the <b>SwitchContinuation + G0</b> seam — the vessel-switch continuation member
        /// boundary (mothership→lander, Fly/Switch-To). An ACCEPTED coverage-handoff, NOT a
        /// position-match: vessel B's first sample need not meet vessel A's terminal. Not a built
        /// geometric seam in v1 (deferred to MissionComposite, design §17).
        /// </summary>
        internal static PhaseSeam SwitchContinuation(bool onCamera = false)
            => new PhaseSeam(PhaseSeamKind.SwitchContinuation, ContinuityOrder.G0, crossing: null, onCamera: onCamera);

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0}+{1}{2}{3}",
                PhaseSeamClassifier.KindToken(Kind),
                Continuity,
                Crossing != null ? " " + Crossing : string.Empty,
                OnCamera ? " onCam" : string.Empty);
    }

    /// <summary>
    /// design §6.1: the seam's kind. NEW enum (extends the live 2-value <c>SeamKind</c> with the
    /// <see cref="SwitchContinuation"/> member-boundary value) — kept separate so the live draw-path
    /// switches on <c>SeamKind</c> are untouched.
    /// </summary>
    internal enum PhaseSeamKind
    {
        /// <summary>No seam (chain start/end, or no neighbour).</summary>
        None = 0,
        /// <summary>Must connect cleanly (ascent↔orbit, orbit↔landing) — numerically enforced (G1).</summary>
        Rigid = 1,
        /// <summary>Tolerated, off-camera discontinuity at an SOI boundary (G0).</summary>
        FlexibleSoi = 2,
        /// <summary>Accepted coverage-handoff at a vessel-switch member boundary (G0, design §6.1).</summary>
        SwitchContinuation = 3,
    }

    /// <summary>
    /// design §6.1: the continuity order a seam asserts. NEW enum (no live equivalent).
    /// </summary>
    internal enum ContinuityOrder
    {
        /// <summary>Position continuity only (the two phases meet in position).</summary>
        G0 = 0,
        /// <summary>Position + tangent continuity (the two phases meet smoothly — velocity direction matches).</summary>
        G1 = 1,
    }
}
