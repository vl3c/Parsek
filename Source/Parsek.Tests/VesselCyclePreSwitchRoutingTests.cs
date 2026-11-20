using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-18: stock's [ / ] vessel cycle behaves as the map Switch-To when it
    /// lands on a vessel that needs Parsek's pre-switch handling (Case A: an armed switch
    /// segment and a different target; Case C: a loaded, separately committed vessel while a
    /// live tree records). The pure routing (<see cref="VesselCycleReFlySwitchPatch.RouteCycle"/>)
    /// keeps stock's candidate order and asks the same <c>DecidePreSwitchDialogAction</c> the
    /// map Switch-To and the alarm Jump ask, for the vessel the press lands on (todo
    /// CYCLE-SWITCH-PRESWITCH-BYPASS).
    /// </summary>
    public class VesselCyclePreSwitchRoutingTests
    {
        private static VesselCycleReFlySwitchPatch.CycleFilter Filter(
            IList<uint> pids, Func<uint, bool> isGhost = null)
            => VesselCycleReFlySwitchPatch.FilterCandidates(pids, isGhost, null);

        // The map Switch-To's own decision for a cycle target (a cycle target is always loaded).
        private static Func<int, bool> PreSwitchFor(IList<uint> pids, bool hasSession, uint sessionFocusedPid,
            bool hasActiveRecording, Func<uint, bool> separateCommitted)
        {
            return i => MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: hasSession,
                priorFocusedPid: sessionFocusedPid,
                newTargetPid: pids[i],
                anotherDialogOpen: false,
                hasActiveRecording: hasActiveRecording,
                targetIsUnloaded: false,
                targetIsSeparateCommittedVessel: separateCommitted(pids[i]))
                == MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog;
        }

        // catches: the cycle bypassing Case C. Nothing to drop (no ghost, no Re-Fly), so the
        // press was left to stock, which switched to the player's separately committed
        // station without committing the live flight first.
        [Fact]
        public void CaseC_NothingDropped_StockPickIsSeparateCommitted_TakesPreSwitch()
        {
            var pids = new List<uint> { 500u, 600u };
            var packed = new List<bool> { false, false };
            var route = VesselCycleReFlySwitchPatch.RouteCycle(Filter(pids), packed, clearToSave: true,
                PreSwitchFor(pids, hasSession: false, sessionFocusedPid: 0u, hasActiveRecording: true,
                    separateCommitted: pid => pid == 500u));

            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.PreSwitch, route.Route);
            Assert.Equal(0, route.TargetIndex);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.LeaveToStock, route.DirectRoute);
        }

        // catches: the cycle bypassing Case A on the Prefix's own switch (a ghost marker
        // dropped): an armed switch segment focused on another vessel is left orphaned.
        [Fact]
        public void CaseA_GhostDropped_KeptTargetDiffersFromSession_TakesPreSwitch()
        {
            var pids = new List<uint> { 4242u, 700u, 800u };
            var packed = new List<bool> { true, false, false };
            var route = VesselCycleReFlySwitchPatch.RouteCycle(Filter(pids, pid => pid == 4242u), packed,
                clearToSave: true,
                PreSwitchFor(pids, hasSession: true, sessionFocusedPid: 100u, hasActiveRecording: true,
                    separateCommitted: _ => false));

            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.PreSwitch, route.Route);
            Assert.Equal(1, route.TargetIndex);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.ForceSwitch, route.DirectRoute);
        }

        // catches: the fix changing GC-4. A cycle between members of one tree (no session,
        // target not a separate committed vessel) routes exactly as before.
        [Fact]
        public void SameTree_NoSession_RoutesAsToday()
        {
            var pids = new List<uint> { 500u, 600u };
            var packed = new List<bool> { false, false };
            Func<int, bool> pre = PreSwitchFor(pids, hasSession: false, sessionFocusedPid: 0u,
                hasActiveRecording: true, separateCommitted: _ => false);

            var left = VesselCycleReFlySwitchPatch.RouteCycle(Filter(pids), packed, true, pre);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.LeaveToStock, left.Route);
            Assert.Equal(0, left.TargetIndex);

            var withGhost = new List<uint> { 4242u, 600u };
            var forced = VesselCycleReFlySwitchPatch.RouteCycle(Filter(withGhost, pid => pid == 4242u),
                new List<bool> { true, false }, true,
                PreSwitchFor(withGhost, false, 0u, true, _ => false));
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.ForceSwitch, forced.Route);
            Assert.Equal(1, forced.TargetIndex);
        }

        // catches: Case A's same-target filter lost on the cycle path.
        [Fact]
        public void CaseA_SameTargetAsSession_RoutesAsToday()
        {
            var pids = new List<uint> { 100u };
            var route = VesselCycleReFlySwitchPatch.RouteCycle(Filter(pids), new List<bool> { false }, true,
                PreSwitchFor(pids, hasSession: true, sessionFocusedPid: 100u, hasActiveRecording: true,
                    separateCommitted: _ => false));
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.LeaveToStock, route.Route);
        }

        // catches: a recording committed for a switch stock refuses (every kept candidate
        // packed, the game not clear to save), the map Switch-To's StockWillRefuse rule.
        [Fact]
        public void StockRefusedSwitch_NeverAsksThePreSwitch()
        {
            int asked = 0;
            Func<int, bool> pre = _ => { asked++; return true; };

            var fallback = VesselCycleReFlySwitchPatch.RouteCycle(
                Filter(new List<uint> { 4242u, 700u }, pid => pid == 4242u),
                new List<bool> { true, true }, clearToSave: false, pre);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.SetActiveFallback, fallback.Route);
            Assert.Equal(1, fallback.TargetIndex);

            var stockFallback = VesselCycleReFlySwitchPatch.RouteCycle(
                Filter(new List<uint> { 700u, 800u }), new List<bool> { true, true }, clearToSave: false, pre);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.LeaveToStock, stockFallback.Route);
            Assert.Equal(0, asked);
        }

        // catches: the pre-switch asked about a candidate the press does not land on.
        [Fact]
        public void PreSwitch_IsAskedOnlyForTheTarget()
        {
            var askedFor = new List<int>();
            var route = VesselCycleReFlySwitchPatch.RouteCycle(
                Filter(new List<uint> { 4242u, 700u, 800u }, pid => pid == 4242u),
                new List<bool> { true, true, false }, clearToSave: false,
                i => { askedFor.Add(i); return false; });
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.ForceSwitch, route.Route);
            Assert.Equal(2, route.TargetIndex);
            Assert.Equal(new[] { 2 }, askedFor.ToArray());
        }

        [Fact]
        public void NothingKept_HasNoTarget()
        {
            var route = VesselCycleReFlySwitchPatch.RouteCycle(
                Filter(new List<uint> { 4242u }, pid => pid == 4242u),
                new List<bool> { true }, true, _ => true);
            Assert.Equal(VesselCycleReFlySwitchPatch.CycleRoute.NothingKept, route.Route);
            Assert.Equal(-1, route.TargetIndex);
        }

        // catches: the switch-call rule. The cycle switches with ForceSetActiveVessel, which
        // skips ClearToSave, so only a packed target in a game that is not clear to save is
        // refused; the map Switch-To's SetActiveVessel is refused whenever it is not clear.
        [Theory]
        [InlineData(false, false, "CLEAR", false)]
        [InlineData(false, false, "NOT_UNDER_ACCELERATION", true)]
        [InlineData(false, false, null, false)]
        [InlineData(true, false, "NOT_UNDER_ACCELERATION", false)]
        [InlineData(true, true, "NOT_UNDER_ACCELERATION", true)]
        [InlineData(true, true, "CLEAR", false)]
        public void SwitchWillBeRefused_Matrix(bool cycleSwitch, bool targetPacked, string clear, bool expected)
        {
            Assert.Equal(expected,
                MapFocusObjectOnSelectPatch.SwitchWillBeRefused(cycleSwitch, targetPacked, clear));
        }

        // catches: RunCycle not routing through the shared pre-switch handling, or a dialog
        // handler finishing a cycle with the map's SetActiveVessel.
        [Fact]
        public void RunCycle_RoutesThroughTheSharedPreSwitchHandling()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string cycle = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Patches", "VesselCycleReFlySwitchPatch.cs"));
            int run = cycle.IndexOf("internal static bool RunCycle(", StringComparison.Ordinal);
            Assert.True(run >= 0);
            string body = cycle.Substring(run);
            Assert.Contains("RouteCycle(", body);
            Assert.Contains("MapFocusObjectOnSelectPatch.TryTakePreSwitch(", body);

            string map = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Patches", "MapFocusObjectOnSelectPatch.cs"));
            int arm = map.IndexOf("private static void ArmIntentAndSwitchTo(", StringComparison.Ordinal);
            Assert.True(arm >= 0);
            string armBody = map.Substring(arm, map.IndexOf("static void Postfix(", arm, StringComparison.Ordinal) - arm);
            Assert.Contains("SwitchLikeTheVesselCycle(target);", armBody);
            int cyc = map.IndexOf("private static void SwitchLikeTheVesselCycle(", StringComparison.Ordinal);
            Assert.True(cyc >= 0);
            string cycBody = map.Substring(cyc, map.IndexOf("static void Postfix(", cyc, StringComparison.Ordinal) - cyc);
            Assert.Contains("FlightGlobals.ForceSetActiveVessel(target);", cycBody);
            Assert.Contains("FlightInputHandler.ResumeVesselCtrlState(target);", cycBody);
        }
    }
}
