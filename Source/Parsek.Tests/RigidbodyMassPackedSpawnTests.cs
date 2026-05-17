using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-helper tests for <see cref="VesselSpawner.ComputeRigidbodyMassForPackedSpawn"/>
    /// and <see cref="VesselSpawner.FormatPackedSpawnSeedSkipMessage"/>.
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
    ///
    /// The live wrapper <see cref="VesselSpawner.SeedRigidbodyMassesForPackedSpawn"/>
    /// can't be exercised from xUnit because its inner loop calls Unity's
    /// overloaded <c>Part == null</c> operator, which is an ECall method that
    /// throws SecurityException outside the engine runtime (same constraint as
    /// the Quaternion.Slerp / Vector3.Lerp note in RecordingOptimizerTests).
    /// The skip-message format is testable via the extracted pure formatter so
    /// the log-line contract the manual checklist relies on does not silently
    /// drift.
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

        [Fact]
        public void NegativeAggregateMass_ClampsToMinimumRBMass()
        {
            // Fails if: a buggy IPartMassModifier mod produces a net-negative
            // moduleMass so partMass + resourceMass + physicslessChildMass < 0,
            // and the helper returns a negative rb.mass that PhysX rejects /
            // re-treats as static. The two Mathf.Max stages must lift the
            // result to MinimumRBMass for the common case (MinimumMass = 0).
            float seeded = VesselSpawner.ComputeRigidbodyMassForPackedSpawn(
                partMass: -5.0f,
                resourceMass: 0f,
                physicslessChildMass: 0f,
                minimumMass: 0f,
                minimumRBMass: 0.001f);

            Assert.True(seeded == 0.001f, $"expected exact MinimumRBMass=0.001f, got {seeded}");
        }

        [Fact]
        public void SkipMessage_NullVessel_HasVesselNullFlagAndContext()
        {
            // Fails if: a future refactor drops the early-return null guard
            // or the warn log format. The log line is part of the
            // bug-recurrence signature in
            // docs/dev/manual-testing/fix-spawn-leg-cascade-explode.md; silent
            // skips would mask a missing-fix regression on every spawn.
            string msg = VesselSpawner.FormatPackedSpawnSeedSkipMessage(
                logContext: "test-null-vessel",
                vesselNull: true,
                partsNull: false);

            Assert.Contains("SeedRigidbodyMassesForPackedSpawn skipped for test-null-vessel", msg);
            Assert.Contains("vesselNull=T", msg);
            Assert.Contains("partsNull=F", msg);
            Assert.Contains("rb.mass=1 default", msg);
        }

        [Fact]
        public void SkipMessage_NullPartsList_HasPartsNullFlag()
        {
            // Fails if: the partsNull branch of the skip-warn message drops
            // its T/F flag or its context, so the manual checklist can't tell
            // whether the warn was the vessel-null path or the parts-null
            // path. The parts-null shape happens when ProtoVessel.Load lands
            // a Vessel with no instantiated parts (mid-failure or partial
            // load).
            string msg = VesselSpawner.FormatPackedSpawnSeedSkipMessage(
                logContext: "test-parts-null",
                vesselNull: false,
                partsNull: true);

            Assert.Contains("test-parts-null", msg);
            Assert.Contains("vesselNull=F", msg);
            Assert.Contains("partsNull=T", msg);
        }
    }
}
