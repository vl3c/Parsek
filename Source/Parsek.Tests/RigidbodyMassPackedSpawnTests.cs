using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-helper tests for <see cref="VesselSpawner.ComputeRigidbodyMassForPackedSpawn"/>.
    ///
    /// Background: KSP's FlightIntegrator updates Part.rb.mass only for unpacked
    /// parts; Parsek-spawned packed vessels keep Unity's rb.mass=1 default until
    /// first unpack. That broke ForceHeaviest autostrut anchor selection on
    /// terminal-orbit spawns: every part tied at mass=1, the distance
    /// tiebreaker in Part.MassivePartCheck kicked in, and ForceHeaviest legs
    /// anchored to the nearest sibling rather than the actual heaviest part.
    /// Seeding rb.mass right after pv.Load with this formula keeps the anchor
    /// selection on the real heaviest part and prevents the unpack-time
    /// cascade-explode.
    /// </summary>
    public class RigidbodyMassPackedSpawnTests
    {
        [Fact]
        public void Tank_FullFuel_ReturnsDryPlusResourceMass()
        {
            // Fails if: a fuel tank's seeded rb.mass omits resource mass and
            // ForceHeaviest then ranks the dry pod heavier than the loaded tank.
            float seeded = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.0f,           // Rockomax16.BW dry
                resourceMass: 7.255f,     // 653 LF * 0.005 + 798 Ox * 0.005
                physicslessChildMass: 0f,
                minimumMass: 0f,
                minimumRBMass: 0.001f);

            Assert.InRange(seeded, 8.254f, 8.256f);
        }

        [Fact]
        public void EmptyTank_NoResource_ReturnsDryMassOnly()
        {
            // Fails if: an empty tank's seeded mass falls back to a minimum
            // rather than the dry mass, distorting the autostrut ranking.
            float seeded = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.0f,
                resourceMass: 0f,
                physicslessChildMass: 0f,
                minimumMass: 0f,
                minimumRBMass: 0.001f);

            Assert.InRange(seeded, 0.999f, 1.001f);
        }

        [Fact]
        public void TinyPart_BelowMinimumRBMass_ClampsExactlyToMinimumRBMass()
        {
            // Fails if: a sub-MinimumRBMass dry-mass part (e.g., a flag decal)
            // keeps a 0 rb.mass and PhysX flags it as a static body. Returns
            // exactly MinimumRBMass because Mathf.Max picks the larger float
            // literal verbatim; no FP imprecision is possible here.
            float seeded = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 0.0001f,
                resourceMass: 0f,
                physicslessChildMass: 0f,
                minimumMass: 0f,
                minimumRBMass: 0.001f);

            Assert.True(seeded == 0.001f, $"expected exact 0.001f, got {seeded}");
        }

        [Fact]
        public void MinimumMass_HigherThanPartMass_ClampsToMinimumMass()
        {
            // Fails if: a part defining a MinimumMass floor (rare inflatables /
            // procedural parts) gets a seeded rb.mass below that floor and
            // diverges from the FlightIntegrator value applied at unpack.
            // KSP's FlightIntegrator does Mathf.Clamp(num, MinimumMass,
            // Mathf.Abs(num)). When num < MinimumMass and num > 0 this is
            // Clamp(value, min, max) with value < min < max-coincides-with-min,
            // and Unity's Clamp resolves the value < min branch first, returning
            // MinimumMass. Mathf.Max(num, MinimumMass) produces the same value
            // and is what the helper uses.
            float seeded = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 0.05f,
                resourceMass: 0f,
                physicslessChildMass: 0f,
                minimumMass: 0.2f,
                minimumRBMass: 0.001f);

            Assert.True(seeded == 0.2f, $"expected exact 0.2f, got {seeded}");
        }

        [Fact]
        public void Engine_DrymassOnly_RanksAboveLightLandingLeg()
        {
            // Fails if: the seeded mass formula returns the same value for a
            // 1.75t engine and a 0.1t leg, which would let the unfixed
            // tiebreaker-on-equal-mass logic re-introduce the bad anchor.
            float engineMass = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.75f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);
            float legMass = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 0.1f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);

            Assert.True(engineMass > legMass);
            Assert.InRange(engineMass, 1.749f, 1.751f);
            Assert.InRange(legMass, 0.099f, 0.101f);
        }

        [Fact]
        public void FullTank_RanksAboveDryPod_AndEngineAndLeg()
        {
            // Fails if: seeded mass ordering doesn't reproduce the real
            // heaviest-part chain a Kerbal-X-shaped vessel needs at autostrut
            // time (tank-with-fuel > pod > engine > leg). This is the exact
            // ordering that placing autostrut anchors on the tank requires.
            float tank = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.0f, resourceMass: 7.255f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);
            float pod = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 2.6f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);
            float engine = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.75f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);
            float leg = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 0.1f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);

            Assert.True(tank > pod);
            Assert.True(pod > engine);
            Assert.True(engine > leg);
        }

        [Fact]
        public void PhysicslessChildren_RollUpIntoParentMass()
        {
            // Fails if: physicsless decorative children (flag decals,
            // physicsless ladders, fairing remnants) are dropped from the
            // parent's seeded rb.mass and the parent then ranks below a
            // near-tied sibling at autostrut time. Matches the
            // GetPhysicslessChildMass rollup FlightIntegrator does at unpack.
            float parentWithoutDecoration = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.0f, resourceMass: 0f, physicslessChildMass: 0f,
                minimumMass: 0f, minimumRBMass: 0.001f);
            float parentWithDecoration = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: 1.0f, resourceMass: 0f, physicslessChildMass: 0.5f,
                minimumMass: 0f, minimumRBMass: 0.001f);

            Assert.True(parentWithDecoration > parentWithoutDecoration);
            Assert.InRange(parentWithDecoration, 1.499f, 1.501f);
        }
    }
}
