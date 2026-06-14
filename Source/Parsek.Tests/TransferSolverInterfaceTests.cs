using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Workstream B1 (design §6.9): guards that the <see cref="ITransferSolver"/> seam is the verbatim
    /// swap-a-library boundary — the default <see cref="UvLambertTransferSolver"/> must be byte-for-byte
    /// behaviour-identical to calling <see cref="UvLambert.Solve"/> directly, so routing the synthesizer
    /// through the interface changes nothing. The math itself is guarded by <c>UvLambertTests</c>; this
    /// class guards only the delegation (success path, both velocities, and fail-closed).
    ///
    /// What makes it fail: the default impl reorders/drops an out param, or the seam introduces any
    /// behavioural difference from the concrete solver.
    /// </summary>
    public class TransferSolverInterfaceTests
    {
        private const double MuEarthKm = 398600.0;

        [Fact]
        public void DefaultSolver_MatchesUvLambert_OnTheCurtisCase()
        {
            var r1 = new Vector3d(5000.0, 10000.0, 2100.0);
            var r2 = new Vector3d(-14600.0, 2500.0, 7000.0);

            bool okDirect = UvLambert.Solve(MuEarthKm, r1, r2, 3600.0, true,
                out Vector3d dv1, out Vector3d dv2);
            // The seam now carries the plane-normal handedness axis; Vector3d.zero => legacy cross.z path,
            // so it must still be byte-identical to the 7-arg direct call.
            bool okSeam = UvLambertTransferSolver.Default.Solve(MuEarthKm, r1, r2, 3600.0, true, Vector3d.zero,
                out Vector3d sv1, out Vector3d sv2);

            Assert.Equal(okDirect, okSeam);
            Assert.True(okSeam);
            // Identical, not merely close — it is the same call.
            Assert.Equal(dv1.x, sv1.x);
            Assert.Equal(dv1.y, sv1.y);
            Assert.Equal(dv1.z, sv1.z);
            Assert.Equal(dv2.x, sv2.x);
            Assert.Equal(dv2.y, sv2.y);
            Assert.Equal(dv2.z, sv2.z);
        }

        [Fact]
        public void DefaultSolver_FailsClosed_LikeUvLambert_OnDegenerateGeometry()
        {
            // Collinear endpoints (~0/180-degree transfer): the underlying solver returns false; the seam
            // must report the same and zero the velocities (fail-closed contract).
            var r1 = new Vector3d(7000.0, 0.0, 0.0);
            var r2 = new Vector3d(14000.0, 0.0, 0.0);

            bool okDirect = UvLambert.Solve(MuEarthKm, r1, r2, 1800.0, true,
                out Vector3d dv1, out Vector3d dv2);
            bool okSeam = UvLambertTransferSolver.Default.Solve(MuEarthKm, r1, r2, 1800.0, true, Vector3d.zero,
                out Vector3d sv1, out Vector3d sv2);

            Assert.Equal(okDirect, okSeam);
            Assert.False(okSeam);
            // Identical delegation: the out velocities match the direct call exactly (both zeroed).
            Assert.Equal(dv1.x, sv1.x);
            Assert.Equal(dv1.y, sv1.y);
            Assert.Equal(dv1.z, sv1.z);
            Assert.Equal(dv2.x, sv2.x);
            Assert.Equal(dv2.y, sv2.y);
            Assert.Equal(dv2.z, sv2.z);
        }

        [Fact]
        public void Synthesizer_TransferSolverSeam_DefaultsToUvLambertDelegation()
        {
            // The synthesizer's injection point defaults to the verbatim delegation, so production
            // behaviour is unchanged until a library impl is deliberately swapped in.
            Assert.IsType<UvLambertTransferSolver>(ReaimTransferSynthesizer.TransferSolver);
        }
    }
}
