namespace Parsek.Reaim
{
    /// <summary>
    /// Workstream B (design §6.9): the replaceable boundary for the pure Lambert transfer solve.
    /// The orbital MATH behaviour is unchanged — this is the swap-a-library seam, so a library
    /// implementation can replace the hand-rolled <see cref="UvLambert"/> with zero render-side
    /// impact. The render module (Workstream A) is downstream of <c>ReaimPlaybackResolver</c> and
    /// already <c>OrbitSegment</c>-only, so it does NOT depend on this interface (the plan confirms A
    /// does not depend on B's internals).
    ///
    /// <para>Contract is exactly <see cref="UvLambert.Solve"/>: fail-closed (return false on
    /// degenerate geometry / non-convergence; the caller steps to the next window), pure (no Unity
    /// scene state, no shared mutable state), consistent units (mu m^3/s^2, positions m, tof s →
    /// velocities m/s, or km throughout). Any swapped implementation MUST pass <c>UvLambertTests</c>
    /// (Curtis 5.2 textbook case, round-trips, degenerate fail-closed).</para>
    /// </summary>
    internal interface ITransferSolver
    {
        /// <summary>Solve Lambert's problem; see <see cref="UvLambert.Solve"/> for the full contract.</summary>
        bool Solve(double mu, Vector3d r1, Vector3d r2, double tof, bool prograde,
            out Vector3d v1, out Vector3d v2);
    }

    /// <summary>
    /// Default <see cref="ITransferSolver"/>: a thin, behaviour-identical delegation to the existing
    /// pure <see cref="UvLambert.Solve"/>. This is the verbatim seam the design calls for — the math
    /// lives in <see cref="UvLambert"/> and is guarded by <c>UvLambertTests</c>; this type only routes
    /// the call so the synthesizer depends on the interface, not the concrete solver.
    /// </summary>
    internal sealed class UvLambertTransferSolver : ITransferSolver
    {
        /// <summary>Process-wide default instance (stateless, so a singleton is safe).</summary>
        internal static readonly UvLambertTransferSolver Default = new UvLambertTransferSolver();

        public bool Solve(double mu, Vector3d r1, Vector3d r2, double tof, bool prograde,
            out Vector3d v1, out Vector3d v2)
            => UvLambert.Solve(mu, r1, r2, tof, prograde, out v1, out v2);
    }
}
