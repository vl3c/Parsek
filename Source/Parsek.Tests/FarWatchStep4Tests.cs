using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The far watch's step 4 (design-far-watch.md 18, "Live follow anywhere"): a ghost at any
    /// body is a far start, the drone's velocity carried across an SOI hop of the ghost, the
    /// stale rotating-frame flags a cross-body jump clears (6.3 step 2), the drone's observed
    /// dominant-body check, the Watch affordances' body half, the far-watch entry that never
    /// resets the loop phase, and the Missions window's Watch target at any body.
    /// </summary>
    public class FarWatchStep4DecisionTests
    {
        // catches: a ghost at another body inside 300 km read as a near watch (today's near
        // watch refuses another body, so W would do nothing).
        [Fact]
        public void NearWatch_IsTheShipsBodyInside300KmOnly()
        {
            Assert.True(FarWatchDecisions.IsWithinNearWatch(true, 299999.0));
            Assert.False(FarWatchDecisions.IsWithinNearWatch(true, 300000.0));
            Assert.False(FarWatchDecisions.IsWithinNearWatch(false, 100.0));
            Assert.False(FarWatchDecisions.IsWithinNearWatch(true, double.NaN));
            Assert.False(FarWatchDecisions.IsWithinNearWatch(true, double.PositiveInfinity));
        }

        [Theory]
        [InlineData(true, true, true, false)]
        [InlineData(true, true, false, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, false, false)]
        public void DirectFarStart_AnyGhostNotWithinTodaysNearWatch(bool follow, bool onShipBody, bool withinRange, bool direct)
        {
            Assert.Equal(direct, FarWatchDecisions.IsDirectFarStart(follow, onShipBody, withinRange));
        }

        // catches: the takeover keeping its step-3 body term (a near-watched ghost that left the
        // ship's SOI would snap back at 305 km instead of being followed).
        [Fact]
        public void Takeover_HasNoBodyTerm()
        {
            Assert.True(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, 12e6));
        }

        // catches: the velocity carried with the wrong sign or without the swizzle: the drone
        // would leave the hop at twice (or zero) the Mun's orbital speed relative to Kerbin.
        [Fact]
        public void CarryVelocity_AddsTheFromBodyFrameVelocityMinusTheTargets_SwizzledToWorld()
        {
            var vMun = new Vector3d(10.0, 20.0, 30.0);
            var munFrameZup = new Vector3d(100.0, 200.0, 0.0);
            var kerbinFrameZup = new Vector3d(1.0, 2.0, 3.0);
            Vector3d v = FarWatchDecisions.CarryObtVelocityAcrossBodies(vMun, munFrameZup, kerbinFrameZup);
            // (munFrame - kerbinFrame) = (99, 198, -3) Zup; .xzy = (99, -3, 198) world.
            Assert.Equal(10.0 + 99.0, v.x, 9);
            Assert.Equal(20.0 - 3.0, v.y, 9);
            Assert.Equal(30.0 + 198.0, v.z, 9);

            Vector3d back = FarWatchDecisions.CarryObtVelocityAcrossBodies(v, kerbinFrameZup, munFrameZup);
            Assert.Equal(vMun.x, back.x, 9);
            Assert.Equal(vMun.y, back.y, 9);
            Assert.Equal(vMun.z, back.z, 9);
        }

        [Fact]
        public void GeeWindow_RestartsOnABodyChangeOnly()
        {
            Assert.True(FarWatchDecisions.ShouldRestartGeeWindow("Kerbin", "Mun"));
            Assert.False(FarWatchDecisions.ShouldRestartGeeWindow("Mun", "Mun"));
            Assert.False(FarWatchDecisions.ShouldRestartGeeWindow(null, "Mun"));
        }

        [Fact]
        public void RecordingBodyMatchKey_ChangesWithEitherBody()
        {
            Assert.NotEqual(FarWatchDecisions.RecordingBodyMatchKey("Mun", "Kerbin"),
                FarWatchDecisions.RecordingBodyMatchKey("Mun", "Mun"));
            Assert.Equal("Mun|none", FarWatchDecisions.RecordingBodyMatchKey("Mun", null));
        }

        [Fact]
        public void OverlayClause_NamesTheGhostsBody()
        {
            Assert.Equal("warp limited: near Mun", FarWatchDecisions.BuildOverlayClause(0.0, double.NaN, "floor", "Mun"));
        }
    }

    public class FarWatchStep4CameraDroneTests
    {
        private static List<CameraDrone.CrossBodyFrameClear> Plan(string target, string dominant, params object[] bodies)
        {
            var names = new List<string>();
            var flags = new List<bool>();
            for (int i = 0; i < bodies.Length; i += 2)
            {
                names.Add((string)bodies[i]);
                flags.Add((bool)bodies[i + 1]);
            }
            return CameraDrone.PlanCrossBodyFrameClears(names, flags, target, dominant);
        }

        // catches: the FWP-1 stale flag: a jump from the Mun (held low, rotating frame) to
        // Kerbin that leaves Mun.inverseRotation set, so Planetarium.Zup keeps turning at the
        // Mun's rate and the follow error grows by r * (omega_Kerbin - omega_Mun).
        [Fact]
        public void MunToKerbin_ClearsTheMunThroughStock()
        {
            var plan = Plan("Kerbin", "Mun", "Sun", false, "Kerbin", false, "Mun", true, "Minmus", false);
            Assert.Single(plan);
            Assert.Equal("Mun", plan[0].BodyName);
            Assert.True(plan[0].ViaStock);
        }

        [Fact]
        public void KerbinToMun_ClearsKerbin_AndAStaleNonDominantBodyDirectly()
        {
            var plan = Plan("Mun", "Kerbin", "Kerbin", true, "Mun", false, "Minmus", true);
            Assert.Equal(2, plan.Count);
            Assert.Equal("Kerbin", plan[0].BodyName);
            Assert.True(plan[0].ViaStock);
            Assert.Equal("Minmus", plan[1].BodyName);
            Assert.False(plan[1].ViaStock);
        }

        // The target's own flag is CheckReferenceFrame's (it sets or clears it by altitude).
        [Fact]
        public void TheTargetsFlag_IsNeverCleared()
        {
            Assert.Empty(Plan("Mun", "Kerbin", "Kerbin", false, "Mun", true));
            Assert.Empty(Plan("Mun", "Mun", "Mun", true));
            Assert.Empty(CameraDrone.PlanCrossBodyFrameClears(null, null, "Mun", "Kerbin"));
        }

        [Fact]
        public void BodyChangeLine_Is16_2sSoiFormat()
        {
            Assert.Equal("drone body Kerbin -> Mun recordingBody=Mun match=True pid=7",
                CameraDrone.FormatBodyChangeLine("Kerbin", "Mun", "Mun", 7));
            Assert.Equal("drone body Kerbin -> Mun recordingBody=Kerbin match=False pid=7",
                CameraDrone.FormatBodyChangeLine("Kerbin", "Mun", "Kerbin", 7));
            Assert.Equal("drone body Mun -> Kerbin recordingBody=none match=n/a pid=7",
                CameraDrone.FormatBodyChangeLine("Mun", "Kerbin", null, 7));
        }
    }

    public class FarWatchStep4DominantBodyPatchTests
    {
        // catches: the drone left to stock's check (VesselPrecalculate runs UpdateOrbit ->
        // CheckDominantBody before ParsekFlight.FixedUpdate, so on the seam tick stock would move
        // the drone across the SOI ahead of the mover and the mover would see no hop to clear
        // the frame for), or the drone taking the ghost route's log (it must say why it refused).
        [Fact]
        public void Route_GhostSkipped_DroneRefused_RealVesselStock()
        {
            Assert.Equal(DominantBodyCheckRoute.Skip, GhostOrbitDominantBodyPatch.DecideDominantBodyCheck(true, false));
            Assert.Equal(DominantBodyCheckRoute.Skip, GhostOrbitDominantBodyPatch.DecideDominantBodyCheck(true, true));
            Assert.Equal(DominantBodyCheckRoute.SkipDrone, GhostOrbitDominantBodyPatch.DecideDominantBodyCheck(false, true));
            Assert.Equal(DominantBodyCheckRoute.RunStock, GhostOrbitDominantBodyPatch.DecideDominantBodyCheck(false, false));
        }

        // catches: a refusal line on every tick (stock would change nothing), or a refusal
        // worded like a stock transition (FW-5 forbids none of it, but the lanes read it).
        [Fact]
        public void RefusedCheck_SilentWhenStockWouldChangeNothing_NamesTheBodyOtherwise()
        {
            Assert.Null(GhostOrbitDominantBodyPatch.DescribeRefusedDroneCheck("Mun", "Mun", 5));
            string line = GhostOrbitDominantBodyPatch.DescribeRefusedDroneCheck("Kerbin", "Mun", 5);
            Assert.Equal("drone dominant-body check refused pid=5 referenceBody=Kerbin mainBody=Mun (the far-watch mover makes the hop)", line);
            Assert.DoesNotContain("stock SOI transition", line);
            Assert.NotNull(GhostOrbitDominantBodyPatch.DescribeRefusedDroneCheck(null, "Mun", 5));
        }

        // catches: the refusal not reporting "no change" to the caller (UpdateOrbit would then
        // RecalculateOrbit the drone anyway) or the Prefix running stock for the drone.
        [Fact]
        public void Prefix_RefusesTheDroneWithNoChange()
        {
            string src = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek")),
                "Patches", "GhostOrbitDominantBodyPatch.cs"));
            int prefix = src.IndexOf("static bool Prefix(", StringComparison.Ordinal);
            int stock = src.IndexOf("if (route == DominantBodyCheckRoute.RunStock)", prefix, StringComparison.Ordinal);
            int noChange = src.IndexOf("__result = false;", stock, StringComparison.Ordinal);
            int drone = src.IndexOf("if (route == DominantBodyCheckRoute.SkipDrone)", noChange, StringComparison.Ordinal);
            int refuse = src.IndexOf("return false;", drone, StringComparison.Ordinal);
            Assert.True(prefix > 0 && stock > prefix && noChange > stock && drone > noChange && refuse > drone);
            Assert.DoesNotContain("static void Postfix(", src);
        }

        [Fact]
        public void FirstFollowTick_OnlySeeds()
        {
            Assert.True(FarWatchDecisions.ShouldSeedFollowSample(false));
            Assert.False(FarWatchDecisions.ShouldSeedFollowSample(true));
        }
    }

    public class FarWatchStep4WatchBodyTests
    {
        private static WatchModeController.WatchBodyDecision Decision(string ghostBody, string activeBody,
            bool hasState = true)
        {
            return WatchModeController.ResolveWatchSameBodyDecision(
                hasState, ghostBody, RenderingZone.Physics, false, null, activeBody);
        }

        [Fact]
        public void DecisionAgainstAnotherBody_ReusesTheGhostsEvidence()
        {
            WatchModeController.WatchBodyDecision d = Decision("Mun", "Mun");
            Assert.True(d.OnSameBody);
            Assert.False(WatchModeController.ResolveWatchBodyDecisionAgainst(d, "Kerbin").OnSameBody);
            Assert.True(WatchModeController.ResolveWatchBodyDecisionAgainst(Decision("Kerbin", "Mun"), "Kerbin").OnSameBody);
            Assert.False(WatchModeController.ResolveWatchBodyDecisionAgainst(Decision("Mun", "Mun", hasState: false), "Mun").OnSameBody);
        }

        // catches: the Watch buttons keeping the step-3 same-body term (a ghost at the Mun
        // greyed "different body" while the far watch could follow it).
        [Fact]
        public void Affordance_AnotherBody_IsWatchableOnlyWithAFarWatch()
        {
            Assert.Equal(WatchModeController.WatchBodyAffordance.SameBody,
                WatchModeController.ResolveWatchBodyAffordance(Decision("Kerbin", "Kerbin"), false));
            Assert.Equal(WatchModeController.WatchBodyAffordance.OtherBodyFarWatch,
                WatchModeController.ResolveWatchBodyAffordance(Decision("Mun", "Kerbin"), true));
            Assert.Equal(WatchModeController.WatchBodyAffordance.OtherBodyRefused,
                WatchModeController.ResolveWatchBodyAffordance(Decision("Mun", "Kerbin"), false));
            Assert.Equal(WatchModeController.WatchBodyAffordance.Unknown,
                WatchModeController.ResolveWatchBodyAffordance(Decision(null, "Kerbin"), true));
            Assert.Equal(WatchModeController.WatchBodyAffordance.Unknown,
                WatchModeController.ResolveWatchBodyAffordance(Decision("Mun", "Kerbin", hasState: false), true));
        }

        // catches: the far watch's entry resetting a looping ghost to its loop start after the
        // drone was written at the ghost's current phase (the camera would land at the pad
        // while the drone sits at the Mun).
        [Fact]
        public void FarWatchEntry_NeverResetsTheLoopPhase()
        {
            Assert.True(WatchModeController.ShouldResetLoopPhaseForWatch(true, true, false, false, false));
            Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(true, true, false, false, false, farWatchEntry: true));
        }
    }

    public class FarWatchStep4MissionWatchTests
    {
        private static int Pick(bool[] member, bool[] ghost, bool[] body, bool[] range, string[] refusal,
            int watched, out bool watching, out string farRefusal)
        {
            return MissionsWindowUI.PickMissionWatchTarget(member.Length, i => member[i], watched,
                i => ghost[i], i => body[i],
                (int i, out string r) => { r = refusal[i]; return range[i]; },
                out watching, out farRefusal);
        }

        // catches: the Missions Watch keeping the ship's-body term (a mission whose lander is
        // at the Mun greyed "Nothing from this mission is flying right now").
        [Fact]
        public void TheFirstMemberAtAWatchableBodyAndRange_IsTheTarget()
        {
            int t = Pick(new[] { true, true, true }, new[] { true, true, true }, new[] { false, true, true },
                new[] { true, true, true }, new string[3], -1, out bool watching, out string why);
            Assert.Equal(1, t);
            Assert.False(watching);
            Assert.Null(why);
        }

        [Fact]
        public void ARefusedMember_GivesTheReason_AndTheWatchedMemberIsReported()
        {
            int t = Pick(new[] { false, true, true }, new[] { true, true, false }, new[] { true, true, true },
                new[] { true, false, true }, new[] { null, "unsafe", null }, 2, out bool watching, out string why);
            Assert.Equal(-1, t);
            Assert.True(watching);
            Assert.Equal("unsafe", why);
        }
    }

    /// <summary>Source gates for the step 4 wiring no headless cell can reach.</summary>
    public class FarWatchStep4WiringGateTests
    {
        private static string Read(string rel)
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek"));
            return File.ReadAllText(Path.Combine(root, rel));
        }

        // catches: the step-3 "ghost left the ship's body" end coming back, or the hop
        // velocity reverting to the cross-body finite difference.
        [Fact]
        public void TickFollow_FollowsAcrossBodies_AndCarriesTheVelocity()
        {
            string src = Read("Controllers/FarWatchController.cs");
            Assert.DoesNotContain("GhostLeftBody", src);
            Assert.Contains("FarWatchDecisions.CarryObtVelocityAcrossBodies(lastVObt", src);
            Assert.Contains("ShouldHandBackForDistance(state, onShipBody, distToShip)", src);
            Assert.Contains("CameraDrone.WriteState(drone, body, world, vObt, recordingBody)", src);
            // The first follow tick seeds the finite difference before any velocity or write.
            int seed = src.IndexOf("FarWatchDecisions.ShouldSeedFollowSample(followSampled)", StringComparison.Ordinal);
            int vel = src.IndexOf("Vector3d rfrm = body.getRFrmVel(world);", StringComparison.Ordinal);
            int write = src.IndexOf("CameraDrone.WriteState(drone, body, world, vObt, recordingBody)", StringComparison.Ordinal);
            Assert.True(seed > 0 && vel > seed && write > vel);
            Assert.Contains("followSampled = false;", src);
        }

        // catches: the cross-body write losing its frame clear, or the clear moving after the
        // write (the first write at the new body would run under the stale frame).
        [Fact]
        public void WriteState_ClearsTheStaleFramesBeforeTheWrite()
        {
            string src = Read("Controllers/CameraDrone.cs");
            int clear = src.IndexOf("ClearStaleRotatingFrames(before, body);", StringComparison.Ordinal);
            int write = src.IndexOf("drone.orbit.UpdateFromStateVectors(", StringComparison.Ordinal);
            int check = src.IndexOf("OrbitPhysicsManager.CheckReferenceFrame();", write, StringComparison.Ordinal);
            Assert.True(clear > 0 && write > clear && check > write);
        }

        // catches: a drone SOI change reaching the recorders, or the drone check moving after
        // the ghost early return (which would swallow it with no Warn for FW-5 to forbid).
        [Fact]
        public void OnVesselSOIChanged_WarnsAndIgnoresTheDroneFirst()
        {
            string src = Read("Controllers/ParsekFlight.RecorderSession.cs");
            int handler = src.IndexOf("void OnVesselSOIChanged(", StringComparison.Ordinal);
            int drone = src.IndexOf("CameraDrone.Is(data.host)", handler, StringComparison.Ordinal);
            int ghost = src.IndexOf("GhostMapPresence.IsGhostOrDroneVessel(data.host.persistentId)", handler, StringComparison.Ordinal);
            int recorder = src.IndexOf("recorder?.OnVesselSOIChanged(data);", handler, StringComparison.Ordinal);
            Assert.True(handler > 0 && drone > handler && ghost > drone && recorder > ghost);
            Assert.Contains("onVesselSOIChanged fired for the drone from=", src);
        }

        [Fact]
        public void TheWatchButtons_ReadTheWatchableBodyTerm()
        {
            Assert.Contains("flight.IsGhostOnWatchableBody(ri)", Read("UI/RecordingsTableUI.TableRows.cs"));
            Assert.Contains("flight.IsGhostOnWatchableBody(idx)", Read("UI/RecordingsTableUI.GroupTree.cs"));
            Assert.Contains("flight.IsGhostOnWatchableBody(recIndex)", Read("UI/TimelineWindowUI.cs"));
            Assert.Contains("flight.IsGhostOnWatchableBody", Read("UI/MissionsWindowUI.Interact.cs"));
            string map = Read("Patches/GhostVesselLoadPatch.cs");
            Assert.Contains("flight.IsGhostOnWatchableBody(recIndex)", map);
            Assert.DoesNotContain("\"Watch (Too Far)\"", map);
            Assert.DoesNotContain("\"Watch (Other SOI)\"", map);
        }
    }
}
