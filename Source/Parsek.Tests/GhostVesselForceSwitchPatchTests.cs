using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-4: stock's <c>[</c> / <c>]</c> vessel cycle switches with
    /// <c>FlightGlobals.ForceSetActiveVessel</c> to any LOADED vessel, and a ghost map marker
    /// inside physics range is loaded. Some Parsek patch must refuse that overload for ghosts,
    /// or a one-part marker becomes the active vessel and stock credits progress and contract
    /// parameters to a vessel that does not exist.
    /// </summary>
    [Collection("Sequential")]
    public class GhostVesselForceSwitchPatchTests : IDisposable
    {
        public GhostVesselForceSwitchPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static MethodInfo ForceSetActiveVessel()
        {
            return typeof(FlightGlobals).GetMethod(
                nameof(FlightGlobals.ForceSetActiveVessel), new[] { typeof(Vessel) });
        }

        /// <summary>Patch classes whose TargetMethod() names ForceSetActiveVessel(Vessel).</summary>
        private static Type[] PatchesTargetingForceSwitch()
        {
            MethodInfo target = ForceSetActiveVessel();
            return typeof(ParsekHarmony).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .Where(t =>
                {
                    MethodInfo tm = t.GetMethod("TargetMethod",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (tm == null || tm.GetParameters().Length != 0) return false;
                    try { return Equals(tm.Invoke(null, null), target); }
                    catch (Exception) { return false; }
                })
                .ToArray();
        }

        [Fact]
        public void SomePatch_TargetsForceSetActiveVessel()
        {
            Assert.NotNull(ForceSetActiveVessel());
            Type[] patches = PatchesTargetingForceSwitch();
            Assert.Single(patches);
            Assert.NotNull(patches[0].GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
        }

        [Fact]
        public void TheForceSwitchDecision_RefusesGhostMarkers_Only()
        {
            Type patch = PatchesTargetingForceSwitch().Single();
            MethodInfo decide = patch.GetMethod("ShouldRefuse",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(decide);

            GhostMapPresence.RegisterGhostMapVesselPidForTesting(4242u);
            Assert.True((bool)decide.Invoke(null, new object[] { 4242u }));
            Assert.False((bool)decide.Invoke(null, new object[] { 4243u }));
        }
            [Fact]
        public void CyclePress_DropsGhostMarkers_EvenWithNoReFlySession()
        {
            // Stock ignores ForceSetActiveVessel's answer and copies the candidate's controls onto
            // the active vessel, so a ghost must never reach stock's final step at all.
            var pids = new List<uint> { 4242u, 7u, 8u };
            var f = VesselCycleReFlySwitchPatch.FilterCandidates(pids, p => p == 4242u, null);
            Assert.False(f.LeaveToStock);
            Assert.Equal(1, f.GhostsDropped);
            Assert.Equal(new List<int> { 1, 2 }, f.KeptIndices);
        }

        [Fact]
        public void CyclePress_NoGhostNoSession_IsLeftToStock()
        {
            var f = VesselCycleReFlySwitchPatch.FilterCandidates(new List<uint> { 7u, 8u }, p => false, null);
            Assert.True(f.LeaveToStock);
            Assert.Equal(new List<int> { 0, 1 }, f.KeptIndices);
        }

        [Fact]
        public void CyclePress_ReFlySession_DropsGhostsAndRefused_NamesFirstRefused()
        {
            var pids = new List<uint> { 4242u, 9u, 7u };
            var f = VesselCycleReFlySwitchPatch.FilterCandidates(pids, p => p == 4242u, p => p == 7u);
            Assert.Equal(1, f.GhostsDropped);
            Assert.Equal(1, f.RefusedDropped);
            Assert.Equal(1, f.FirstRefusedIndex);
            Assert.Equal(new List<int> { 2 }, f.KeptIndices);

            var onlyGhosts = VesselCycleReFlySwitchPatch.FilterCandidates(
                new List<uint> { 4242u }, p => true, null);
            Assert.Empty(onlyGhosts.KeptIndices);
            Assert.Equal(-1, onlyGhosts.FirstRefusedIndex);
        }

        [Fact]
        public void CyclePrefix_FiltersWithTheGhostMapCheck()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "Patches", "VesselCycleReFlySwitchPatch.cs");
            string src = System.IO.File.ReadAllText(path);
            Assert.Contains("FilterCandidates(candidatePids, GhostMapPresence.IsGhostMapVessel, isAllowed)", src);
        }
    }
}
