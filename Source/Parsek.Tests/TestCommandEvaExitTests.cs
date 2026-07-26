using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C2 coverage for the EvaExit pure surfaces (<see cref="TestCommandEvaExit"/>).
    /// ResolveKerbalArg: default-first-crew / exact-match / unknown / no-crew (fails if a
    /// typo silently EVAs the wrong kerbal). DecideEvaExitCompletion: every conjunct gates,
    /// and the F7 settleSeconds dwell holds the head (fails if the head advances before the
    /// auto-switch or before the dwell elapsed).
    /// </summary>
    public class TestCommandEvaExitTests
    {
        private const double Budget = 120.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- ResolveKerbalArg -----

        [Fact]
        public void Resolve_NullArg_DefaultsToFirstCrew()
        {
            var crew = new List<string> { "Jebediah Kerman", "Bob Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg(null, crew, out string err);
            Assert.Null(err);
            Assert.Equal("Jebediah Kerman", r);
        }

        [Fact]
        public void Resolve_EmptyArg_DefaultsToFirstCrew()
        {
            var crew = new List<string> { "Valentina Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("", crew, out string err);
            Assert.Null(err);
            Assert.Equal("Valentina Kerman", r);
        }

        [Fact]
        public void Resolve_ExactMatch_ReturnsNamed()
        {
            var crew = new List<string> { "Jebediah Kerman", "Bob Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("Bob Kerman", crew, out string err);
            Assert.Null(err);
            Assert.Equal("Bob Kerman", r);
        }

        [Fact]
        public void Resolve_Unknown_ErrorsKerbalNotAboard()
        {
            var crew = new List<string> { "Jebediah Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("Bill Kerman", crew, out string err);
            Assert.Null(r);
            Assert.Equal("kerbal-not-aboard", err);
        }

        [Fact]
        public void Resolve_CaseSensitive_ErrorsKerbalNotAboard()
        {
            // Ordinal exact match: a case-off name is not a hit.
            var crew = new List<string> { "Jebediah Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("jebediah kerman", crew, out string err);
            Assert.Null(r);
            Assert.Equal("kerbal-not-aboard", err);
        }

        [Fact]
        public void Resolve_EmptyCrew_ErrorsNoCrew()
        {
            string r = TestCommandEvaExit.ResolveKerbalArg(null, new List<string>(), out string err);
            Assert.Null(r);
            Assert.Equal("no-crew", err);
        }

        [Fact]
        public void Resolve_NullCrew_ErrorsNoCrew()
        {
            string r = TestCommandEvaExit.ResolveKerbalArg("Jeb", null, out string err);
            Assert.Null(r);
            Assert.Equal("no-crew", err);
        }

        // ----- DecideEvaExitCompletion -----

        [Fact]
        public void Complete_AllConjuncts_NoRelease_NoDwell_Ok()
        {
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, evaVesselExists: true, evaVesselIsActive: true, sceneSettled: true,
                    releaseRequested: false, releaseApplied: false, settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_NotExists_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, false, true, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_NotActive_StillWaiting()
        {
            // Exists but the auto-switch has not made it the active vessel yet.
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, false, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_NotSettled_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, false, false, false, true, Budget));
        }

        [Fact]
        public void Complete_ReleaseRequestedNotApplied_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, releaseRequested: true, releaseApplied: false,
                    settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_ReleaseRequestedAndApplied_Ok()
        {
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, releaseRequested: true, releaseApplied: true,
                    settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_DwellNotElapsed_StillWaiting()
        {
            // F7: the base conjuncts hold but the settleSeconds dwell has not elapsed -> hold
            // the head so Parsek's deferred EVA auto-record arms before the next FIFO command.
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, false, false, settleElapsed: false, Budget));
        }

        [Fact]
        public void Complete_BudgetExpired_ExitTimeout()
        {
            Assert.Equal(EvaExitCompletionDecision.ExitTimeout,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    Budget, false, false, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_PositiveBeatsBudget()
        {
            // Complete even past budget: a settled active EVA vessel is OK, not a timeout.
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    Budget + 10.0, true, true, true, false, false, true, Budget));
        }

        // ----- DecideLadderRelease (EVA-1 flight-2 release false-positive fix) -----

        private const int MaxFires = 3;

        [Fact]
        public void Ladder_NotOnLadder_ConcludesNoop()
        {
            // Off the ladder with no prior fire: the noop path (kerbal exited not on a ladder).
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.NotOnLadder,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: false, inReceptiveLetGoState: false, attemptCount: 0, MaxFires));
        }

        [Fact]
        public void Ladder_OffLadderAfterFire_ConcludesVerified()
        {
            // Off the ladder AFTER a fire: verified-left (RunEvent's synchronous transition took).
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.NotOnLadder,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: false, inReceptiveLetGoState: true, attemptCount: 1, MaxFires));
        }

        [Fact]
        public void Ladder_OnLadder_TransitionalState_Waits()
        {
            // THE REGRESSION: on a ladder but in st_ladder_acquire (not a receptive let-go state)
            // ~0.2s after exit. The old applier fired here -> silent no-op -> false released=true.
            // The fix WAITS for the timed transition into a receptive state; it never fires here.
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.WaitForReceptiveState,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: false, attemptCount: 0, MaxFires));
        }

        [Fact]
        public void Ladder_OnLadder_ReceptiveState_Fires()
        {
            // On a ladder in st_ladder_idle / climb / descend / end_reached: On_ladderLetGo is
            // registered, so RunEvent will actually transition -> fire.
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.Fire,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: true, attemptCount: 0, MaxFires));
        }

        [Fact]
        public void Ladder_ReceptiveState_FiresUpToCapMinusOne()
        {
            // attemptCount below the cap still fires (bounded re-fire while receptive).
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.Fire,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: true, attemptCount: MaxFires - 1, MaxFires));
        }

        [Fact]
        public void Ladder_StillOnLadderAtCap_Exhausts()
        {
            // Fired the cap and still on a ladder (mod re-grab / stuck FSM): bounded give-up so
            // the EvaExit budget is not burned. Exhaustion wins even from a receptive state.
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.ExhaustedStillOnLadder,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: true, attemptCount: MaxFires, MaxFires));
        }

        [Fact]
        public void Ladder_ExhaustionBeatsTransitionalWait()
        {
            // At the cap, even a non-receptive state exhausts rather than waiting forever.
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.ExhaustedStillOnLadder,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: false, attemptCount: MaxFires, MaxFires));
        }

        [Fact]
        public void Ladder_OffLadderBeatsExhaustion()
        {
            // Off the ladder always concludes NotOnLadder, even at/over the cap (a late departure
            // still counts as verified-left, never a spurious exhaustion).
            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.NotOnLadder,
                TestCommandEvaExit.DecideLadderRelease(
                    onLadder: false, inReceptiveLetGoState: false, attemptCount: MaxFires, MaxFires));
        }

        [Fact]
        public void Ladder_MaxFiresCap_IsThree()
        {
            Assert.Equal(3, TestCommandEvaExit.LadderReleaseMaxFires);
        }

        [Fact]
        public void Ladder_EveryRunEventThrowing_ExhaustsBoundedWithZeroFires()
        {
            // Simulates the applier's counter bookkeeping under a RunEvent that ALWAYS throws
            // (KerbalFSM.RunEvent does when the fsm was never started): attempts increment
            // BEFORE the call, fires only AFTER it returns - so fires stays 0 forever.
            // Two properties must hold together, and they are why the two counters are separate:
            //   liveness - the cap is fed by attempts, so the poll loop terminates bounded
            //              instead of burning the whole EvaExit budget;
            //   honesty  - fires stays 0, so a later organic ladder departure reports
            //              released=false, never a "verified-left" we did not cause.
            int attempts = 0, fires = 0, polls = 0;
            TestCommandEvaExit.LadderReleaseAction action;
            while (true)
            {
                polls++;
                Assert.True(polls <= MaxFires + 2, "release polling did not terminate bounded");
                action = TestCommandEvaExit.DecideLadderRelease(
                    onLadder: true, inReceptiveLetGoState: true, attemptCount: attempts, MaxFires);
                if (action != TestCommandEvaExit.LadderReleaseAction.Fire) break;
                attempts++;            // counted before the (throwing) RunEvent call
                                       // fires++ never runs: RunEvent threw
            }

            Assert.Equal(TestCommandEvaExit.LadderReleaseAction.ExhaustedStillOnLadder, action);
            Assert.Equal(MaxFires, attempts);
            Assert.Equal(0, fires);
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void Payload_CarriesKerbalPidReleased()
        {
            var p = TestCommandEvaExit.BuildCompletePayload("Bob Kerman", 12345u, released: true);
            Assert.Equal("Bob Kerman", Val(p, "kerbal"));
            Assert.Equal("12345", Val(p, "evaPid"));
            Assert.Equal("true", Val(p, "released"));
            Assert.Equal(new[] { "kerbal", "evaPid", "released", "standoff" },
                         p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void Payload_NullKerbal_EmptyString_ReleasedFalse()
        {
            var p = TestCommandEvaExit.BuildCompletePayload(null, 0u, released: false);
            Assert.Equal(string.Empty, Val(p, "kerbal"));
            Assert.Equal("0", Val(p, "evaPid"));
            Assert.Equal("false", Val(p, "released"));
            // Default = the stage was never requested. Every scenario but EVA-4.
            Assert.Equal("off", Val(p, "standoff"));
        }

        // ----- Post-release standoff (EVA-4 flight-3 canopy-cut mitigation) -----
        //
        // THE DEFECT (proved from logs/2026-07-25_1310_EVA-4-atmo-chute/KSP.log + the
        // decompiled KerbalEVA): the kerbal let go of the pod's hatch ladder at 13:09:53.614,
        // its chute reached SEMIDEPLOYED at .668, and at .868 - 200 ms later, with the pod
        // still essentially in contact - the canopy was CUT and the kerbal was in Ragdoll
        // (the single "Event Stumble not assigned to state Ragdoll" line at .884 is the
        // NEXT frame of the same contact, rejected because the FSM had already moved).
        // On_stumble is registered on st_semi_deployed_parachute and goes to st_ragdoll;
        // leaving that state to anything but full deploy calls evaChute.CutParachute().
        // So the mitigation is separation before the canopy, and it is measured, not timed.

        [Fact]
        public void Standoff_ZeroMeans_Off_AndNeverWaits()
        {
            // Every non-declaring scenario: the stage is inert whatever the distance reads.
            Assert.True(TestCommandEvaExit.StandoffClearThisPoll(0.0, 0.0));
            Assert.True(TestCommandEvaExit.StandoffClearThisPoll(double.NaN, 0.0));
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Cleared,
                         TestCommandEvaExit.ClassifyStandoff(0.0, 0, 999.0, 8.0, 1650.0, 500.0));
            Assert.Equal("off", TestCommandEvaExit.StandoffToken(
                0.0, TestCommandEvaExit.EvaExitStandoffState.Cleared));
        }

        [Theory]
        [InlineData(0.0, false)]     // in contact - the flight-3 frame
        [InlineData(29.9, false)]    // just short
        [InlineData(30.0, true)]     // inclusive bound
        [InlineData(90.0, true)]     // the measured separation 3.8 s after the cut
        public void Standoff_ComparesTheObservedDistanceInclusively(double nearest, bool clear)
            => Assert.Equal(clear, TestCommandEvaExit.StandoffClearThisPoll(nearest, 30.0));

        [Fact]
        public void Standoff_NoOtherLoadedVessel_ReadsClear()
        {
            // PositiveInfinity = nothing that can collide. There is no collider, so there
            // is no stumble, so there is nothing to wait for.
            Assert.True(TestCommandEvaExit.StandoffClearThisPoll(
                double.PositiveInfinity, 30.0));
        }

        [Fact]
        public void Standoff_UnreadableDistance_FailsClosed()
        {
            // NaN is NOT separation. Fail-closed here is safe because the wait is bounded.
            Assert.False(TestCommandEvaExit.StandoffClearThisPoll(double.NaN, 30.0));
        }

        [Fact]
        public void Standoff_RequiresTheDebounceRun_NotOneGoodFrame()
        {
            // One clear poll is not separation: a floating-origin shift or a frame where a
            // vessel's position has not been updated can read clear once.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Waiting,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 1, 1.0, 8.0, 1650.0, 500.0));
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Cleared,
                         TestCommandEvaExit.ClassifyStandoff(
                             30.0, TestCommandEvaExit.StandoffDebouncePolls, 1.0, 8.0,
                             1650.0, 500.0));
        }

        [Fact]
        public void Standoff_BoundedWait_GivesUpAndSaysSo()
        {
            // NON-FATAL by design: an unchuted kerbal is a certain death, a contact risk is
            // not. The give-up must therefore be visible on the wire instead of terminal.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.GaveUp,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 0, 8.0, 8.0, 1650.0, 500.0));
            Assert.Equal("timeout", TestCommandEvaExit.StandoffToken(
                30.0, TestCommandEvaExit.EvaExitStandoffState.GaveUp));
        }

        [Fact]
        public void Standoff_ClearedBeatsTheBudgetOnTheSamePoll()
        {
            // A poll that is BOTH past the bound and debounced-clear is a success, not a
            // give-up: the separation was actually observed.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Cleared,
                         TestCommandEvaExit.ClassifyStandoff(
                             30.0, TestCommandEvaExit.StandoffDebouncePolls, 99.0, 8.0,
                             1650.0, 500.0));
        }

        [Fact]
        public void Standoff_TokenDistinguishesNeverAskedFromGaveUp()
        {
            // A bare bool would make "we did not ask" and "we asked and it never cleared"
            // read identically in the collected artifacts.
            Assert.Equal("off", TestCommandEvaExit.StandoffToken(
                0.0, TestCommandEvaExit.EvaExitStandoffState.GaveUp));
            Assert.Equal("cleared", TestCommandEvaExit.StandoffToken(
                30.0, TestCommandEvaExit.EvaExitStandoffState.Cleared));
            Assert.Equal("timeout", TestCommandEvaExit.StandoffToken(
                30.0, TestCommandEvaExit.EvaExitStandoffState.GaveUp));
        }

        [Fact]
        public void Standoff_AnUnconcludedStageIsNotReportedAsCleared()
        {
            // Reachable only on the ExitTimeout path (the standoff gives up bounded, so it
            // cannot itself cause that timeout). Reporting a still-Waiting stage as
            // "cleared" would claim an observation that was never made - the exact shape of
            // the defect this whole change exists to close.
            Assert.Equal("waiting", TestCommandEvaExit.StandoffToken(
                30.0, TestCommandEvaExit.EvaExitStandoffState.Waiting));
            Assert.Equal("off", TestCommandEvaExit.StandoffToken(
                0.0, TestCommandEvaExit.EvaExitStandoffState.Waiting));
        }

        [Fact]
        public void Standoff_TheWallClockBoundStaysInsideItsMeasuredFreeFallCost()
        {
            // BOTH directions, because the first draft only guarded one and got the other
            // badly wrong. FLOOR: the bound must still cover the measured clear time (a 30 m
            // standoff was reached ~2.3 s after release on this profile), or the mitigation
            // gives up before it can engage. CEILING: the kerbal is UNCHUTED for the whole
            // stage, so the bound is paid in FREE FALL - measured on the flight-3 log at
            // ~1,016 m per 15 s. An earlier revision sized this against "~165 m" (15 s x the
            // ladder-attached -11 m/s) and was wrong by ~6x, which is how a bound long
            // enough to fly the kerbal into the ground came to look cheap. 10 s of free fall
            // from a standing-ish start is already ~600 m, which is most of EVA-4's
            // [700, 2100] m window, so the wall clock alone must never be the safety bound.
            Assert.True(TestCommandEvaExit.StandoffMaxWaitSeconds >= 5.0);
            Assert.True(TestCommandEvaExit.StandoffMaxWaitSeconds <= 10.0);
            Assert.True(TestCommandEvaExit.StandoffDebouncePolls >= 2);
        }

        [Fact]
        public void Standoff_GivesUpAtTheAltitudeFloorWhateverTheClockSays()
        {
            // THE BLOCKER THIS CLOSES: the mission may hand off anywhere in its EVA window
            // (EVA-4's floor is 700 m). Bounded by wall clock ALONE, a low handoff plus a
            // standoff that never clears flies the kerbal into the ground with the canopy
            // never armed - strictly worse than the collision the stage exists to avoid.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.GaveUp,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 0, 1.0, 8.0, 480.0, 500.0));
            // Above the floor and inside the clock: still waiting.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Waiting,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 0, 1.0, 8.0, 520.0, 500.0));
            // Inclusive at the floor.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.GaveUp,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 0, 1.0, 8.0, 500.0, 500.0));
        }

        [Fact]
        public void Standoff_NoFloorRequested_IsWallClockOnly()
        {
            // floor=0 keeps the pre-floor behaviour for any caller that does not declare one.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Waiting,
                         TestCommandEvaExit.ClassifyStandoff(30.0, 0, 1.0, 8.0, 3.0, 0.0));
        }

        [Fact]
        public void Standoff_UnreadableAltitudeDoesNotAbandonTheStage()
        {
            // An unreadable frame must not be mistaken for "out of sky"; the wall clock
            // still bounds the stage, so failing OPEN here cannot hang it.
            foreach (double alt in new[] { double.NaN, double.PositiveInfinity })
                Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Waiting,
                             TestCommandEvaExit.ClassifyStandoff(30.0, 0, 1.0, 8.0, alt, 500.0));
        }

        [Fact]
        public void Standoff_ClearedBeatsTheAltitudeFloorToo()
        {
            // A poll that both satisfies the debounce and sits under the floor is a success:
            // the separation really was observed.
            Assert.Equal(TestCommandEvaExit.EvaExitStandoffState.Cleared,
                         TestCommandEvaExit.ClassifyStandoff(
                             30.0, TestCommandEvaExit.StandoffDebouncePolls, 1.0, 8.0,
                             100.0, 500.0));
        }

        [Fact]
        public void Standoff_DebounceIsARunAndResetsOnAnyNonClearPoll()
        {
            // The run-vs-tally contract, which lived in an untestable applier line until it
            // was extracted. As a TALLY ("+= clear ? 1 : 0") every ClassifyStandoff assertion
            // still passes while the debounce silently degrades to "any two clear polls ever
            // observed" - certifying exactly the glitched single-frame read it exists to
            // reject.
            int run = 0;
            run = TestCommandEvaExit.AdvanceStandoffClearPolls(run, true);
            Assert.Equal(1, run);
            run = TestCommandEvaExit.AdvanceStandoffClearPolls(run, false);
            Assert.Equal(0, run);
            run = TestCommandEvaExit.AdvanceStandoffClearPolls(run, true);
            run = TestCommandEvaExit.AdvanceStandoffClearPolls(run, true);
            Assert.Equal(TestCommandEvaExit.StandoffDebouncePolls, run);
        }
    }
}
