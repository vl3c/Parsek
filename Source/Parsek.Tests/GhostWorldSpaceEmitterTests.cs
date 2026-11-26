using System;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of <see cref="GhostWorldSpaceEmitter"/>: stock's origin-shift rule for a
    /// world-space smoke particle, and the emitter velocity it drifts by. The wiring cells read
    /// source, because the component needs a live particle system (proven in flight).
    /// </summary>
    [Collection("Sequential")]
    public class GhostWorldSpaceEmitterTests : IDisposable
    {
        public GhostWorldSpaceEmitterTests()
        {
            GhostWorldSpaceEmitter.ResetForTesting();
        }

        public void Dispose()
        {
            GhostWorldSpaceEmitter.ResetForTesting();
        }

        private static void AssertClose(Vector3 expected, Vector3 actual)
        {
            Assert.Equal((double)expected.x, (double)actual.x, 3);
            Assert.Equal((double)expected.y, (double)actual.y, 3);
            Assert.Equal((double)expected.z, (double)actual.z, 3);
        }

        [Fact]
        public void ShiftParticle_NoDrift_MovesByTheNegativeShift()
        {
            Vector3 p = GhostWorldSpaceEmitter.ShiftParticle(
                new Vector3(1f, 2f, 3f), new Vector3d(45.0, -10.0, 0.5), Vector3.zero, 0.7f);
            AssertClose(new Vector3(-44f, 12f, 2.5f), p);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.5f, -22.4f)]
        [InlineData(1f, -44.8f)]
        public void ShiftParticle_DriftIsTheRandomPartOfOneStep(float random, float expectedX)
        {
            // Orbital speed over one 0.02 s step: 44.8 m.
            Vector3 p = GhostWorldSpaceEmitter.ShiftParticle(
                Vector3.zero, Vector3d.zero, new Vector3(44.8f, 0f, 0f), random);
            AssertClose(new Vector3(expectedX, 0f, 0f), p);
        }

        [Fact]
        public void WorldVelocity_GhostCarriedByTheFrame_ReadsTheFrameVelocity()
        {
            // The active vessel flies at 2240 m/s beside a ghost at rest relative to it: the
            // transform does not move, the world does.
            Vector3 v = GhostWorldSpaceEmitter.WorldVelocity(
                Vector3.zero, new Vector3d(44.8, 0.0, 0.0), 0.02);
            AssertClose(new Vector3(2240f, 0f, 0f), v);
        }

        [Fact]
        public void WorldVelocity_LandedViewer_ReadsTheGhostsOwnMotion()
        {
            Vector3 v = GhostWorldSpaceEmitter.WorldVelocity(
                new Vector3(0f, 4f, 0f), Vector3d.zero, 0.02);
            AssertClose(new Vector3(0f, 200f, 0f), v);
        }

        [Fact]
        public void WorldVelocity_OriginResetAlone_IsNotMotion()
        {
            // A 600 m origin reset moves the ghost's transform by -600 m and the world by the same.
            Vector3 v = GhostWorldSpaceEmitter.WorldVelocity(
                new Vector3(-600f, 0f, 0f), new Vector3d(600.0, 0.0, 0.0), 0.02);
            AssertClose(Vector3.zero, v);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-30.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void WorldVelocity_ClockNotAdvanced_IsZero(double seconds)
        {
            AssertClose(Vector3.zero, GhostWorldSpaceEmitter.WorldVelocity(
                new Vector3(500f, 0f, 0f), Vector3d.zero, seconds));
        }

        [Fact]
        public void Slower_RejectsTheOneStepJumpOfALoopRestart()
        {
            Vector3 cruise = new Vector3(0f, 150f, 0f);
            Vector3 jump = new Vector3(-400000f, 0f, 0f);
            AssertClose(cruise, GhostWorldSpaceEmitter.Slower(cruise, jump));
            AssertClose(cruise, GhostWorldSpaceEmitter.Slower(jump, cruise));
        }

        [Theory]
        [InlineData(1.0, 2.0, 3.0, true)]
        [InlineData(double.NaN, 0.0, 0.0, false)]
        [InlineData(0.0, double.PositiveInfinity, 0.0, false)]
        [InlineData(double.PositiveInfinity, double.NegativeInfinity, 0.0, false)]
        public void IsUsableShift_RefusesWhatStockRefuses(double x, double y, double z, bool usable)
        {
            Assert.Equal(usable, GhostWorldSpaceEmitter.IsUsableShift(new Vector3d(x, y, z)));
        }

        [Fact]
        public void OnOriginShifted_SumsUsableShiftsAndSkipsTheRest()
        {
            GhostWorldSpaceEmitter.OnOriginShifted(new Vector3d(1.0, 0.0, 0.0), new Vector3d(44.0, 0.0, 0.0));
            GhostWorldSpaceEmitter.OnOriginShifted(new Vector3d(double.NaN, 0.0, 0.0), Vector3d.zero);
            GhostWorldSpaceEmitter.OnOriginShifted(new Vector3d(0.0, 5.0, 0.0), Vector3d.zero);
            Vector3d total = GhostWorldSpaceEmitter.TotalShiftForTesting;
            Assert.Equal(45.0, total.x, 9);
            Assert.Equal(5.0, total.y, 9);
            Assert.Equal(0, GhostWorldSpaceEmitter.RegisteredCountForTesting);
        }

        // ---- the standing ghost's smoke flow (owner ruling 2026-11-24: the floor stays)

        [Fact]
        public void StandingFlow_AStandingGhostsSmokeLeavesAtTheFloorSpeed()
        {
            // restock-fx-srb-smoke-1: 1 m/s down the emitter's own -Y; the exhaust axis is -Y.
            Vector3 own = new Vector3(0f, -1f, 0f);
            Vector3 extra = GhostWorldSpaceEmitter.StandingFlow(
                own, Vector3.down, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, 0f);
            AssertClose(new Vector3(0f, -6f, 0f), own + extra);
        }

        [Theory]
        [InlineData(5f)]     // 6 - 5 = the asset's own 1 m/s: nothing to add
        [InlineData(6f)]
        [InlineData(27f)]
        [InlineData(104f)]
        [InlineData(2240f)]
        [InlineData(float.NaN)]
        public void StandingFlow_NeverTouchesAMovingGhost(float ghostSpeed)
        {
            Vector3 extra = GhostWorldSpaceEmitter.StandingFlow(
                new Vector3(0f, -1f, 0f), Vector3.down, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, ghostSpeed);
            Assert.True(extra == Vector3.zero, extra.ToString());
        }

        [Fact]
        public void StandingFlow_FadesOutAsTheGhostGathersSpeed()
        {
            Vector3 own = new Vector3(0f, -1f, 0f);
            Vector3 extra = GhostWorldSpaceEmitter.StandingFlow(
                own, Vector3.down, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, 2f);
            AssertClose(new Vector3(0f, -4f, 0f), own + extra);
        }

        [Fact]
        public void StandingFlow_FollowsTheExhaustAxisNotTheAssetsOwn()
        {
            Vector3 own = new Vector3(0f, -1f, 0f);
            Vector3 extra = GhostWorldSpaceEmitter.StandingFlow(
                own, Vector3.right, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, 0f);
            AssertClose(new Vector3(6f, 0f, 0f), own + extra);
        }

        [Fact]
        public void Wiring_TheFloorIsAStandingFlowAndNeverTheEmittersOwnVelocity()
        {
            string source = Source(Path.Combine("Ghost", "GhostVisualBuilder.FxSetup.cs"));
            Assert.Contains("origin.standingAxis = floored / WorldSpaceEmitterFloorSpeed;", source);
            Assert.DoesNotContain("localVelocity = floored", source);
        }

        private static string Source(string relative)
            => File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", relative));

        [Fact]
        public void Wiring_TheOriginShiftPatchDrivesTheGhosts()
        {
            Assert.Contains("GhostWorldSpaceEmitter.OnOriginShifted(refPos, nonFrame);",
                Source(Path.Combine("Patches", "FloatingOriginSetOffsetPatch.cs")));
        }

        [Fact]
        public void Wiring_TheFxCloneIsRegisteredOnceAndItsFXPrefabStripped()
        {
            string source = Source(Path.Combine("Ghost", "GhostVisualBuilder.FxSetup.cs"));
            int strip = source.IndexOf("case \"FXPrefab\":", StringComparison.Ordinal);
            int attach = source.IndexOf("GhostWorldSpaceEmitter.AttachAll(fxClone);", StringComparison.Ordinal);
            // A system both kept in FloatingOrigin's list and registered here would move twice.
            Assert.True(strip >= 0 && strip < attach);
            Assert.Contains("Object.Destroy(behaviours[i]);", source.Substring(strip, attach - strip));
        }

        // ---- the FX Parsek builds itself (GHOST-TRANSIENT-FX-RIDE-ACTIVE-VESSEL)

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, false, false)]  // local space moves with its parent
        [InlineData(true, true, false)]    // already moved once
        public void ShouldAttach_WorldSpaceSystemsOnceEach(bool worldSpace, bool attached, bool expected)
        {
            Assert.Equal(expected, GhostWorldSpaceEmitter.ShouldAttach(worldSpace, attached));
        }

        [Theory]
        [InlineData("GhostVisualBuilder.TransientFx.cs", "GhostWorldSpaceEmitter.AttachOwn(obj);", 2)]
        [InlineData("GhostVisualBuilder.TransientFx.cs", "GhostWorldSpaceEmitter.AttachOwn(dustObj);", 1)]
        [InlineData("GhostVisualBuilder.HeatAndReentryFx.cs", "GhostWorldSpaceEmitter.AttachOwn(fireObj);", 1)]
        public void Wiring_EveryParsekBuiltWorldSpaceFxFollowsTheOrigin(string file, string call, int count)
        {
            Assert.Equal(count, Count(Source(Path.Combine("Ghost", file)), call));
        }

        [Fact]
        public void Wiring_TheWorldSpaceSystemsParsekBuildsAreTheOnesAttached()
        {
            // Five in TransientFx (explosion fireball and smoke, puff smoke and sparks, launch
            // dust: three AttachOwn roots) and the reentry fire; the EVA plume is local. A new
            // world-space system reds here until it is attached.
            string transient = Source(Path.Combine("Ghost", "GhostVisualBuilder.TransientFx.cs"));
            string reentry = Source(Path.Combine("Ghost", "GhostVisualBuilder.HeatAndReentryFx.cs"));
            Assert.Equal(5, Count(transient, "simulationSpace = ParticleSystemSimulationSpace.World;"));
            Assert.Equal(1, Count(reentry, "simulationSpace = ParticleSystemSimulationSpace.World;"));
        }

        private static int Count(string source, string needle)
        {
            int found = 0;
            for (int i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + 1, StringComparison.Ordinal))
                found++;
            return found;
        }

        [Fact]
        public void Wiring_ParsekBuiltSystemsShiftWithoutDriftAndAFreeRootMovesItself()
        {
            string source = Source(Path.Combine("Ghost", "GhostWorldSpaceEmitter.cs"));
            int own = source.IndexOf("internal static int AttachOwn(GameObject fxObject)", StringComparison.Ordinal);
            int should = source.IndexOf("internal static bool ShouldAttach(", StringComparison.Ordinal);
            Assert.True(own >= 0 && own < should);
            string body = source.Substring(own, should - own);
            Assert.Contains("e.drifts = false;", body);
            Assert.Contains("root.movesTransform = true;", body);
            int move = source.IndexOf("private static void MoveParticles(", StringComparison.Ordinal);
            Assert.Contains("if (e.movesTransform)", source.Substring(move));
            int late = source.IndexOf("private void LateUpdate()", StringComparison.Ordinal);
            Assert.Contains("if (!drifts) return;", source.Substring(late, 300));
        }

        [Fact]
        public void OwnersReplaceSetOffset_ReadsKspCommunityFixesOnly()
        {
            Assert.True(GhostWorldSpaceEmitter.OwnersReplaceSetOffset(new[] { "com.parsek.mod", "KSPCommunityFixes" }));
            Assert.False(GhostWorldSpaceEmitter.OwnersReplaceSetOffset(new[] { "com.parsek.mod" }));
            Assert.False(GhostWorldSpaceEmitter.OwnersReplaceSetOffset(new string[0]));
        }

        [Fact]
        public void Wiring_RegistrationLivesExactlyAsLongAsTheEnabledComponent()
        {
            string source = Source(Path.Combine("Ghost", "GhostWorldSpaceEmitter.cs"));
            int enable = source.IndexOf("private void OnEnable()", StringComparison.Ordinal);
            int disable = source.IndexOf("private void OnDisable()", StringComparison.Ordinal);
            int late = source.IndexOf("private void LateUpdate()", StringComparison.Ordinal);
            Assert.True(enable >= 0 && enable < disable && disable < late);
            Assert.Contains("registered.Add(this);", source.Substring(enable, disable - enable));
            Assert.Contains("registered.Remove(this);", source.Substring(disable, late - disable));
        }
    }
}
