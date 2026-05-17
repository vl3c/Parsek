using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-helper tests for <see cref="VesselSpawner.ComputeRigidbodyMassForPackedSpawn"/>
    /// (the FlightIntegrator-matching rb.mass formula) and
    /// <see cref="PartMassivePartCheckSeederPatch.ShouldSeedPart"/> (the
    /// patch's pure boolean gate).
    ///
    /// Background: KSP's FlightIntegrator updates Part.rb.mass only for
    /// unpacked parts; freshly-loaded packed parts keep Unity's rb.mass=1
    /// default until first Unpack. That broke ForceHeaviest autostrut
    /// anchor selection: every part tied at mass=1, the distance tiebreaker
    /// in Part.MassivePartCheck kicked in, and ForceHeaviest legs anchored
    /// to the nearest sibling rather than the actual heaviest part. The
    /// production fix is the Harmony prefix on Part.MassivePartCheck that
    /// lazily seeds each part's rb.mass at the read site; the formula
    /// helper below is exercised by it, and the gate predicate's branches
    /// are tested individually.
    ///
    /// The live patch wrapper itself can't be invoked from xUnit because
    /// reading <c>p.rb</c> / <c>p.vessel</c> goes through Unity's
    /// overloaded <c>Part == null</c> operator (an ECall method that
    /// throws SecurityException outside the engine runtime, same
    /// constraint as the Quaternion.Slerp / Vector3.Lerp note in
    /// RecordingOptimizerTests). The
    /// <see cref="PartMassivePartCheckSeederTest.SeededPartCount_IsPositiveInFlight"/>
    /// in-game test (Spawner / FLIGHT) is the canary for "patch is
    /// registered but body is a no-op."
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

        // Pure-decision tests for the Part.MassivePartCheck prefix gate.
        // The live wrapper can't be invoked from xUnit because reading
        // `p.rb` / `p.vessel` goes through Unity's overloaded `Part == null`
        // operator (same ECall SecurityException constraint that forced the
        // VesselSpawner skip-message formatter split in PR #885). The gate
        // predicate takes seven booleans rather than a Part, so every
        // branch is testable directly.
        //
        // Convention: helper builds the all-pass tuple and tests flip one
        // boolean at a time to pin each skip reason.

        private static bool CallShouldSeedPart(
            bool partNull = false, bool rbNull = false, bool partInfoNull = false,
            bool partPacked = true, bool rbMassAtUnityDefault = true,
            bool vesselNull = false, bool isFlag = false, bool isGhostMap = false)
        {
            return PartMassivePartCheckSeederPatch.ShouldSeedPart(
                partNull, rbNull, partInfoNull, partPacked, rbMassAtUnityDefault,
                vesselNull, isFlag, isGhostMap);
        }

        [Fact]
        public void MassivePartCheckPrefix_HappyPath_SeedsPart()
        {
            // Fails if: a future "skip-by-default" refactor of the gate ever
            // declines to seed a normal packed, rb-bearing part with mass at
            // Unity default. This is the only branch that should actually
            // mutate rb.mass.
            Assert.True(CallShouldSeedPart());
        }

        [Fact]
        public void MassivePartCheckPrefix_NullPart_SkipsSeeding()
        {
            // Fails if: a future refactor passes a null Part to the seeder;
            // SeedSinglePackedPart would NRE before its own null guard.
            Assert.False(CallShouldSeedPart(partNull: true));
        }

        [Fact]
        public void MassivePartCheckPrefix_PartWithoutRigidbody_SkipsSeeding()
        {
            // Fails if: physicsless parts (no rb component) trigger the
            // seeder. The seeder helper itself early-returns on rb==null,
            // but the gate should refuse first to avoid the redundant call
            // and so that the diagnostic counter is not spuriously bumped
            // for parts that were never going to mutate.
            Assert.False(CallShouldSeedPart(rbNull: true));
        }

        [Fact]
        public void MassivePartCheckPrefix_PartWithoutPartInfo_SkipsSeeding()
        {
            // Fails if: a part whose `partInfo` lookup returns null (stub
            // Part from a mod-failure or unrecognized part-name load) passes
            // the gate and the helper silently no-ops, leaving the cascade
            // bug live with no diagnostic trail. The gate must surface this
            // as a tested skip reason rather than relying on the helper's
            // own null-guard.
            Assert.False(CallShouldSeedPart(partInfoNull: true));
        }

        [Fact]
        public void MassivePartCheckPrefix_UnpackedPart_SkipsSeeding()
        {
            // Fails if: unpacked parts (FlightIntegrator-managed rb.mass)
            // are re-seeded. The seeder would rewrite an FI-corrected value
            // (including fuel-consumption mass deltas) back to the
            // initial-mass formula, which is stale by definition once the
            // vessel is in physics.
            Assert.False(CallShouldSeedPart(partPacked: false));
        }

        [Fact]
        public void MassivePartCheckPrefix_RbMassAlreadySeeded_SkipsSeeding()
        {
            // Fails if: a part whose rb.mass has already been seeded (or
            // legitimately differs from Unity's 1.0 default) is re-seeded
            // on every subsequent MassivePartCheck call. The
            // rbMassAtUnityDefault gate provides the implicit idempotence
            // that bounds the seed work to one write per part per packed
            // window.
            Assert.False(CallShouldSeedPart(rbMassAtUnityDefault: false));
        }

        [Fact]
        public void MassivePartCheckPrefix_NullVessel_SkipsSeeding()
        {
            // Fails if: a part disconnected from its vessel (mid-decouple,
            // mid-destroy) triggers the seeder. The downstream gates
            // (isFlag, isGhostMap) cannot evaluate without a vessel
            // reference.
            Assert.False(CallShouldSeedPart(vesselNull: true));
        }

        [Fact]
        public void MassivePartCheckPrefix_FlagVessel_SkipsSeeding()
        {
            // Fails if: flag-spawn vessels (GhostVisualBuilder flag spawns
            // once they reach physics) trigger the seeder. Flags are
            // single-part with autostrutMode=Off; MassivePartCheck should
            // never fire on them in practice, but the gate is defensive
            // so unexpected stock or modded code paths cannot accidentally
            // pull flag parts through the seeder.
            Assert.False(CallShouldSeedPart(isFlag: true));
        }

        [Fact]
        public void MassivePartCheckPrefix_GhostMapVessel_SkipsSeeding()
        {
            // Fails if: ghost-map ProtoVessels (registered in
            // GhostMapPresence.ghostMapVesselPids before pv.Load) trigger
            // the seeder. Same defensive rationale as the flag case.
            Assert.False(CallShouldSeedPart(isGhostMap: true));
        }
    }
}
