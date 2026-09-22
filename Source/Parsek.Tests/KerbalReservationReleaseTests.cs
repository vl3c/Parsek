using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Does a crew reservation from a committed flight LIFT once game time passes the
    /// flight's recorded end?
    ///
    /// <para>Design 9.2 / 9.3 / 9.4 (<c>docs/parsek-game-actions-and-resources-recorder-design.md</c>):
    /// a reservation is one continuous block from UT 0 to the kerbal's latest recorded
    /// end; a RECOVERED end is temporary ("available after recovery"), and when it ends
    /// the owner reclaims his slot - an unused stand-in is deleted, a used one retired.
    /// A rewind to before the end re-reserves him and brings the chain back.</para>
    ///
    /// <para>These cells drive the REAL production walk
    /// (<see cref="LedgerOrchestrator.RecalculateAndPatch"/> and the current-UT cutoff
    /// the live clock paths pass) with the clock before the flight's launch, during it,
    /// at its end and long after, and read what the rest of Parsek reads:
    /// <see cref="KerbalsModule.IsKerbalAvailable"/>,
    /// <see cref="KerbalsModule.ShouldFilterFromCrewDialog"/> (the VAB/SPH crew dialog
    /// filter), the reservation kind, and the roster pass's stand-in lifecycle.</para>
    ///
    /// <para>Filed as KERBAL-RESERVATION-NEVER-LIFTS-WITH-TIME (these cells pinned the
    /// buggy "never lifts" answer until the fix).</para>
    /// </summary>
    [Collection("Sequential")]
    public class KerbalReservationReleaseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string RecordingId = "rec-release-jeb";
        private const double StartUT = 100.0;
        private const double EndUT = 300.0;

        public KerbalReservationReleaseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static string Inv(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static GameAction AddFlight(
            string recordingId, string kerbal, string[] rawCrew,
            double startUT, double endUT, KerbalEndState endState, int sequence)
        {
            var snapshot = CrewSnapshot(rawCrew);
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = recordingId,
                VesselName = "Jumping Flea " + recordingId,
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState> { { kerbal, endState } },
                CrewEndStatesResolved = true
            });

            // The row LedgerOrchestrator.CreateKerbalAssignmentActions writes: stamped at
            // the flight's START, carrying its end state (owner name, reverse-mapped).
            return new GameAction
            {
                UT = startUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
                KerbalRole = "Pilot",
                StartUT = (float)startUT,
                EndUT = (float)endUT,
                KerbalEndStateField = endState,
                Sequence = sequence
            };
        }

        private static void CommitFlight(KerbalEndState endState)
        {
            Ledger.AddAction(AddFlight(RecordingId, Jeb, new[] { Jeb }, StartUT, EndUT, endState, 1));
        }

        private static void AssertHeld(string clock, double expectedUntilUT)
        {
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.NotNull(kerbals);
            Assert.True(kerbals.Reservations.ContainsKey(Jeb),
                "reservation missing at clock " + clock);
            Assert.Equal(expectedUntilUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
            Assert.True(kerbals.IsReservedNow(Jeb), "not held at clock " + clock);
            Assert.True(kerbals.ActiveReservations.ContainsKey(Jeb));
            Assert.False(kerbals.IsKerbalAvailable(Jeb),
                "kerbal reads available at clock " + clock);
            Assert.True(kerbals.ShouldFilterFromCrewDialog(Jeb),
                "kerbal not filtered from the crew dialog at clock " + clock);
            Assert.Equal(KerbalReservationKind.ReservedActive, kerbals.GetReservationKind(Jeb));
        }

        private static void AssertReleased(string clock)
        {
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.NotNull(kerbals);
            // The raw map still names the flight (the Kerbals window reads the hold from it).
            Assert.True(kerbals.Reservations.ContainsKey(Jeb),
                "raw reservation missing at clock " + clock);
            Assert.Equal(EndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb), "still held at clock " + clock);
            Assert.False(kerbals.ActiveReservations.ContainsKey(Jeb));
            Assert.True(kerbals.IsKerbalAvailable(Jeb),
                "kerbal not available at clock " + clock);
            Assert.False(kerbals.ShouldFilterFromCrewDialog(Jeb),
                "kerbal still filtered from the crew dialog at clock " + clock);
            Assert.Equal(KerbalReservationKind.NotManaged, kerbals.GetReservationKind(Jeb));
            Assert.False(kerbals.IsManaged(Jeb));
        }

        // ------------------------------------------------------------------
        // The production walk
        // ------------------------------------------------------------------

        // catches: a Recovered flight holding its kerbal forever (the filed bug).
        [Theory]
        [InlineData(50.0, true)]     // before the flight launched: reserved from UT 0 (design 9.2)
        [InlineData(200.0, true)]    // mid-flight
        [InlineData(299.9, true)]    // just before the recorded end
        [InlineData(300.0, false)]   // exactly the recorded end: free AT the recovery instant
        [InlineData(10000.0, false)] // long after it
        public void RecoveredFlight_HeldBeforeItsEnd_FreeFromItsEnd(double currentUT, bool held)
        {
            CommitFlight(KerbalEndState.Recovered);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(currentUT, "release-test");

            if (held) AssertHeld(Inv(currentUT), expectedUntilUT: EndUT);
            else AssertReleased(Inv(currentUT));
        }

        // catches: the ordinary-play full replay (cutoffUT=null) ignoring the live clock.
        [Fact]
        public void RecoveredFlight_FullReplay_JudgesAgainstTheLiveClock()
        {
            CommitFlight(KerbalEndState.Recovered);
            double clock = 200.0;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;

            LedgerOrchestrator.RecalculateAndPatch();
            AssertHeld("live 200", expectedUntilUT: EndUT);

            clock = 345.68;
            LedgerOrchestrator.RecalculateAndPatch();
            AssertReleased("live 345.68");
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation released: 'Jebediah Kerman' endUT=300.0 nowUT=345.7"));
        }

        // catches: an unreadable clock (early load, headless host) spuriously releasing.
        [Fact]
        public void RecoveredFlight_FullReplay_UnknownClockHolds()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 0.0; // cold-load "not ready"

            LedgerOrchestrator.RecalculateAndPatch();

            AssertHeld("unknown", expectedUntilUT: EndUT);
            Assert.True(double.IsNaN(LedgerOrchestrator.Kerbals.WalkClockUT));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation released"));
        }

        // catches: a scene-change load judging the reservation against Planetarium's
        // stale previous-scene clock instead of the loaded save's own time.
        [Fact]
        public void DuringOnLoad_TheLoadedSaveClockWinsOverAStaleLiveClock()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 10000.0; // stale, later
            KerbalsModule.LoadedSaveUTProviderForTesting = () => 200.0;  // the loaded save
            ParsekScenario.SetOnLoadInProgressForTesting(true);
            try
            {
                // ksp-load passes Planetarium's (stale) UT as the cutoff; it is not trusted.
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "ksp-load");
            }
            finally
            {
                ParsekScenario.SetOnLoadInProgressForTesting(false);
            }

            AssertHeld("onload save 200", expectedUntilUT: EndUT);
        }

        // catches: a rewind to before the end leaving the returned kerbal free.
        [Fact]
        public void RewindBeforeTheEnd_ReReservesAReleasedKerbal()
        {
            CommitFlight(KerbalEndState.Recovered);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "release-test");
            AssertReleased("10000");

            // The rewind paths pass the adjusted rewind UT as the cutoff.
            LedgerOrchestrator.RecalculateAndPatch(200.0);
            AssertHeld("rewind 200", expectedUntilUT: EndUT);

            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation released: 'Jebediah Kerman' endUT=300.0 nowUT=10000.0"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation re-reserved: 'Jebediah Kerman' endUT=300.0 nowUT=200.0"));
        }

        // catches: the Re-Fly post-invoke recalc (cutoff double.MaxValue, "no time filter")
        // judging reservations at the end of time and releasing every Recovered hold -
        // measured on CL-4 run 2026-09-22_2143 as walkClockUT=1.8e308.
        [Fact]
        public void NoTimeFilterCutoff_JudgesAgainstTheLiveClockNotTheEndOfTime()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 200.0;

            LedgerOrchestrator.RecalculateAndPatch(double.MaxValue);

            AssertHeld("MaxValue cutoff, live 200", expectedUntilUT: EndUT);
            Assert.Equal(200.0, LedgerOrchestrator.Kerbals.WalkClockUT);
        }

        // catches: the provisional cutoff engine walk (rows up to the cutoff only) and the
        // authoritative whole-ledger recompute both recording transitions, which logs
        // "released endUT=300" then "re-reserved endUT=600" on EVERY recalc between them.
        [Fact]
        public void CutoffRecalc_TwoFlights_NoFlipFlopBetweenTheProvisionalAndAuthoritativeWalks()
        {
            Ledger.AddAction(AddFlight("rec-a", Jeb, new[] { Jeb }, 100.0, 300.0, KerbalEndState.Recovered, 1));
            Ledger.AddAction(AddFlight("rec-b", Jeb, new[] { Jeb }, 500.0, 600.0, KerbalEndState.Recovered, 2));

            LedgerOrchestrator.RecalculateAndPatch(400.0); // a rewind to 400
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(410.0, "a");
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(420.0, "b");

            AssertHeld("420", expectedUntilUT: 600.0);
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation released"));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation re-reserved"));
            Assert.Contains(logLines, l => l.Contains("PostWalk summary:") && l.Contains("provisional=True"));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(700.0, "c");
            int released = 0;
            foreach (string l in logLines)
                if (l.Contains("Reservation released: 'Jebediah Kerman' endUT=600.0 nowUT=700.0")) released++;
            Assert.Equal(1, released);
        }

        // catches: the pending-rewind clock branch reading RewindContext.RewindAdjustedUT,
        // which EndRewind zeroes in the same OnLoad, so the window between OnLoad and the
        // deferred UT set judged every hold against an unknown clock.
        [Fact]
        public void PendingRewindAdjustment_JudgesAgainstTheCapturedTargetUT()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 10000.0; // pre-rewind future
            RecordingStore.RewindUTAdjustmentPending = true;
            RecordingStore.RewindUTAdjustmentTargetUT = 200.0;
            try
            {
                LedgerOrchestrator.RecalculateAndPatch();
            }
            finally
            {
                RecordingStore.RewindUTAdjustmentPending = false;
                RecordingStore.RewindUTAdjustmentTargetUT = double.NaN;
            }

            AssertHeld("pending rewind target 200", expectedUntilUT: EndUT);
            Assert.Equal(200.0, LedgerOrchestrator.Kerbals.WalkClockUT);
        }

        // catches: a clockless walk re-holding a returned owner, so the roster pass
        // recreates his deleted stand-in and the next clocked walk deletes it again.
        [Fact]
        public void UnknownClockWalk_KeepsTheLastDecision_NoStandInChurn()
        {
            var actions = new List<GameAction>
            {
                AddFlight(RecordingId, Jeb, new[] { Jeb }, StartUT, EndUT, KerbalEndState.Recovered, 1)
            };
            var module = new KerbalsModule();
            var roster = new FakeRoster();
            roster.Add(Jeb, ProtoCrewMember.RosterStatus.Available);
            Walk(module, actions, 200.0);
            module.ApplyToRoster(roster);
            Walk(module, actions, 400.0);
            module.ApplyToRoster(roster);
            Assert.Equal(1, roster.CreatedCount);
            Assert.Equal(1, roster.RemovedCount);

            KerbalsModule.LiveClockUTProviderForTesting = () => 0.0; // no readable clock
            module.Reset();
            module.PrePass(actions, null);
            for (int i = 0; i < actions.Count; i++) module.ProcessAction(actions[i]);
            module.PostWalk();
            module.ApplyToRoster(roster);

            Assert.True(double.IsNaN(module.WalkClockUT));
            Assert.False(module.IsReservedNow(Jeb));
            Assert.Equal(0, roster.RecreatedCount);
            Assert.Equal(1, roster.CreatedCount);
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey(Jeb));
        }

        [Fact]
        public void ResolveHoldWithUnknownClock_KeepsOnlyAnUnchangedDecision()
        {
            Assert.False(KerbalsModule.ResolveHoldWithUnknownClock(Res(300.0), true, false, 300.0));
            Assert.True(KerbalsModule.ResolveHoldWithUnknownClock(Res(300.0), true, true, 300.0));
            // The end moved (a later flight extended the hold): held until a clock says otherwise.
            Assert.True(KerbalsModule.ResolveHoldWithUnknownClock(Res(600.0), true, false, 300.0));
            Assert.True(KerbalsModule.ResolveHoldWithUnknownClock(Res(300.0), false, false, 0.0));
            Assert.True(KerbalsModule.ResolveHoldWithUnknownClock(Res(300.0, permanent: true), true, false, 300.0));
        }

        // catches: a returned owner becoming dismissable while committed flights still name
        // him (a sacked kerbal a rewind must re-reserve), and an unrelated kerbal blocked.
        [Fact]
        public void ReturnedOwner_StaysBlockedFromDismissal()
        {
            CommitFlight(KerbalEndState.Recovered);
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "release-test");
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;

            AssertReleased("10000");
            Assert.True(kerbals.IsNamedByCommittedFlight(Jeb));
            Assert.True(kerbals.ShouldBlockDismissal(Jeb));
            Assert.False(kerbals.ShouldBlockDismissal("Valentina Kerman"));
            Assert.Equal("This kerbal flew a committed flight on your timeline.",
                KerbalDismissalPatch.DescribeDismissalBlock(
                    kerbals.GetReservationKind(Jeb), kerbals.IsNamedByCommittedFlight(Jeb)));
        }

        // catches: a release line printed on every walk instead of once per transition.
        [Fact]
        public void ReleaseLine_PrintsOncePerTransition()
        {
            CommitFlight(KerbalEndState.Recovered);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "a");
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10001.0, "b");
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10002.0, "c");

            int released = 0;
            foreach (string l in logLines)
                if (l.Contains("Reservation released: 'Jebediah Kerman'")) released++;
            Assert.Equal(1, released);
        }

        // catches: the fix leaking into open-ended holds (Aboard never ends by time).
        [Theory]
        [InlineData(200.0)]
        [InlineData(10000.0)]
        public void AboardFlight_ReservationIsOpenEnded_AndDoesNotLift(double currentUT)
        {
            CommitFlight(KerbalEndState.Aboard);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(currentUT, "release-test");

            AssertHeld(Inv(currentUT), expectedUntilUT: double.PositiveInfinity);
        }

        // catches: a later, UNRELATED flight shortening an Aboard hold. Reservations merge
        // by max end, so a Recovered flight in another tree does not end the +inf hold;
        // only a recovery of the Aboard flight's own vessel does (a KerbalRecovered row,
        // KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL; see
        // KerbalRecoveryReservationCloseTests).
        [Fact]
        public void AboardFlight_ALaterRecoveredFlightDoesNotShortenTheOpenEndedHold()
        {
            CommitFlight(KerbalEndState.Aboard);
            Ledger.AddAction(AddFlight("rec-later-recovery", Jeb, new[] { Jeb },
                400.0, 500.0, KerbalEndState.Recovered, 2));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "release-test");

            AssertHeld("10000", expectedUntilUT: double.PositiveInfinity);
        }

        [Fact]
        public void Reservation_Lifts_WhenTheFlightLeavesTheCommittedSet()
        {
            CommitFlight(KerbalEndState.Recovered);
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(200.0, "release-test");
            AssertHeld("200 (before removal)", expectedUntilUT: EndUT);

            Assert.True(RecordingStore.RemoveCommittedById(RecordingId));
            LedgerOrchestrator.RecalculateAndPatch();

            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.False(kerbals.Reservations.ContainsKey(Jeb));
            Assert.True(kerbals.IsKerbalAvailable(Jeb));
            Assert.False(kerbals.ShouldFilterFromCrewDialog(Jeb));
        }

        // ------------------------------------------------------------------
        // The crossed-an-end check (KSC / Tracking Station / crew dialog)
        // ------------------------------------------------------------------

        // catches: KSC / TS time passing a Recovered end without any recalculation, and
        // the check recalculating on every frame.
        [Fact]
        public void ReleaseDueCheck_RecalculatesOnceWhenTheClockReachesTheEnd()
        {
            CommitFlight(KerbalEndState.Recovered);
            double clock = 200.0;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(EndUT, LedgerOrchestrator.Kerbals.NextReservationReleaseUT);

            Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(250.0, "t"));
            AssertHeld("250 (no crossing yet)", expectedUntilUT: EndUT);

            clock = 300.5;
            Assert.True(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(300.5, "t"));
            AssertReleased("300.5");
            Assert.True(double.IsPositiveInfinity(LedgerOrchestrator.Kerbals.NextReservationReleaseUT));

            clock = 400.0;
            Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(400.0, "t"));
            Assert.Contains(logLines, l => l.Contains("Kerbal reservation release due: nextReleaseUT=300")
                && l.Contains("reason=t"));
        }

        // catches: the "already fired" guard swallowing the SAME release after a rewind put
        // it back, so KSC warp past the recovery a second time never frees the kerbal.
        [Fact]
        public void ReleaseDueCheck_ReArmsAfterARewindPutsTheReleaseBack()
        {
            CommitFlight(KerbalEndState.Recovered);
            double clock = 200.0;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;
            LedgerOrchestrator.RecalculateAndPatch();

            clock = 350.0;
            Assert.True(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(350.0, "t"));
            AssertReleased("350");

            LedgerOrchestrator.RecalculateAndPatch(200.0); // the rewind's cutoff walk
            AssertHeld("rewind 200", expectedUntilUT: EndUT);
            Assert.Equal(EndUT, LedgerOrchestrator.Kerbals.NextReservationReleaseUT);

            clock = 360.0;
            Assert.True(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(360.0, "t"));
            AssertReleased("360 after rewind");
        }

        // catches: a triggered walk that could not move the release (no readable clock)
        // re-triggering a full recalculation on every frame.
        [Fact]
        public void ReleaseDueCheck_DoesNotLoopWhenTheWalkCannotMoveTheRelease()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 0.0; // unreadable
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(EndUT, LedgerOrchestrator.Kerbals.NextReservationReleaseUT);

            Assert.True(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(400.0, "t"));
            AssertHeld("unreadable walk clock", expectedUntilUT: EndUT);
            Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(401.0, "t"));
            Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(402.0, "t"));
        }

        [Fact]
        public void ResolveLastTriggeredReleaseUT_ForgetsTheTriggerOnceAnotherWalkRan()
        {
            Assert.Equal(300.0, KerbalsModule.ResolveLastTriggeredReleaseUT(300.0, 7, 7));
            Assert.True(double.IsNaN(KerbalsModule.ResolveLastTriggeredReleaseUT(300.0, 9, 7)));
        }

        // catches: the due-check acting on a load's or a rewind's untrustworthy clock.
        [Fact]
        public void ReleaseDueCheck_StandsDownDuringLoadAndRewindAdjustment()
        {
            CommitFlight(KerbalEndState.Recovered);
            KerbalsModule.LiveClockUTProviderForTesting = () => 200.0;
            LedgerOrchestrator.RecalculateAndPatch();

            ParsekScenario.SetOnLoadInProgressForTesting(true);
            try
            {
                Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(500.0, "t"));
            }
            finally
            {
                ParsekScenario.SetOnLoadInProgressForTesting(false);
            }

            RecordingStore.RewindUTAdjustmentPending = true;
            try
            {
                Assert.False(LedgerOrchestrator.RecalculateIfKerbalReservationReleaseDue(500.0, "t"));
            }
            finally
            {
                RecordingStore.RewindUTAdjustmentPending = false;
            }

            AssertHeld("200 (stood down)", expectedUntilUT: EndUT);
        }

        // ------------------------------------------------------------------
        // Owner return through the displacement machinery (design 9.4 / 9.6)
        // ------------------------------------------------------------------

        private static KerbalsModule Walk(KerbalsModule module, List<GameAction> actions, double nowUT)
        {
            module.Reset();
            module.PrePass(actions, nowUT);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
        }

        // catches: the L3 live shape - a flight committed after its own recovery still
        // generating a stand-in (Brooke Kerman in the 2026-09-02 L3 log).
        [Fact]
        public void CommittedAfterItsRecovery_NoSlotNoStandIn()
        {
            var actions = new List<GameAction>
            {
                AddFlight(RecordingId, Jeb, new[] { Jeb }, 10.0, 342.3, KerbalEndState.Recovered, 1)
            };
            var module = Walk(new KerbalsModule(), actions, 345.68);
            var roster = new FakeRoster();
            roster.Add(Jeb, ProtoCrewMember.RosterStatus.Available);

            module.ApplyToRoster(roster);

            Assert.Equal(0, roster.CreatedCount);
            Assert.False(module.Slots.ContainsKey(Jeb));
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey(Jeb));
            Assert.True(module.IsKerbalAvailable(Jeb));
            Assert.False(module.ShouldFilterFromCrewDialog(Jeb));
            Assert.Contains(logLines, l => l.Contains("PostWalk summary: reservations=1 permanent=0 temporary=1")
                && l.Contains("released=1"));
        }

        // catches: an unused stand-in surviving the owner's return, the owner staying in
        // the swap map (swapped out of the craft he boards), or a rewind generating a
        // fresh name instead of reusing the persisted chain entry.
        [Fact]
        public void UnusedStandIn_DeletedOnReturn_RecreatedByNameOnRewind()
        {
            var actions = new List<GameAction>
            {
                AddFlight(RecordingId, Jeb, new[] { Jeb }, StartUT, EndUT, KerbalEndState.Recovered, 1)
            };
            var module = new KerbalsModule();
            var roster = new FakeRoster();
            roster.Add(Jeb, ProtoCrewMember.RosterStatus.Available);

            Walk(module, actions, 200.0);
            module.ApplyToRoster(roster);
            string standIn = module.Slots[Jeb].Chain[0];
            Assert.Equal(1, roster.CreatedCount);
            Assert.True(roster.Contains(standIn));
            Assert.Equal(standIn, CrewReservationManager.CrewReplacements[Jeb]);
            Assert.Equal(standIn, module.GetActiveOccupant(Jeb));

            // Time passes the recovery: owner reclaims, the unused stand-in is deleted.
            Walk(module, actions, 400.0);
            module.ApplyToRoster(roster);
            Assert.False(roster.Contains(standIn));
            Assert.Equal(1, roster.RemovedCount);
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey(Jeb));
            Assert.Equal(Jeb, module.GetActiveOccupant(Jeb));
            Assert.True(module.IsKerbalAvailable(Jeb));
            Assert.False(module.ShouldFilterFromCrewDialog(Jeb));
            Assert.Equal(standIn, module.Slots[Jeb].Chain[0]); // name kept for rewinds

            // Rewind before the end: re-reserved, the SAME name comes back.
            Walk(module, actions, 200.0);
            module.ApplyToRoster(roster);
            Assert.True(roster.Contains(standIn));
            Assert.Equal(1, roster.CreatedCount);
            Assert.Equal(1, roster.RecreatedCount);
            Assert.Equal(standIn, CrewReservationManager.CrewReplacements[Jeb]);
            Assert.True(module.ShouldFilterFromCrewDialog(Jeb));
        }

        // catches: a stand-in who flew a committed flight being deleted (or left active)
        // when the owner returns, and a rewind not reactivating him.
        [Fact]
        public void UsedStandIn_RetiredOnReturn_ReactivatedOnRewind()
        {
            var actions = new List<GameAction>
            {
                AddFlight(RecordingId, Jeb, new[] { Jeb }, StartUT, EndUT, KerbalEndState.Recovered, 1)
            };
            var module = new KerbalsModule();
            var roster = new FakeRoster();
            roster.Add(Jeb, ProtoCrewMember.RosterStatus.Available);
            Walk(module, actions, 200.0);
            module.ApplyToRoster(roster);
            string standIn = module.Slots[Jeb].Chain[0];

            // The stand-in flies Jeb's seat in a committed flight that ends before Jeb's.
            // The ledger row carries the OWNER (reverse-mapped); the raw crew is the
            // stand-in, which is what makes him "used in a recording".
            actions.Add(AddFlight("rec-standin-flight", Jeb, new[] { standIn },
                210.0, 250.0, KerbalEndState.Recovered, 2));

            Walk(module, actions, 400.0);
            module.ApplyToRoster(roster);
            Assert.Contains(standIn, module.RetiredKerbals);
            Assert.True(roster.Contains(standIn));        // retired, not deleted
            Assert.Equal(0, roster.RemovedCount);
            Assert.True(module.ShouldFilterFromCrewDialog(standIn));
            Assert.False(module.ShouldFilterFromCrewDialog(Jeb));
            Assert.Equal(KerbalReservationKind.ReservedRetired, module.GetReservationKind(standIn));

            Walk(module, actions, 200.0);
            module.ApplyToRoster(roster);
            Assert.DoesNotContain(standIn, module.RetiredKerbals);
            Assert.Equal(standIn, module.GetActiveOccupant(Jeb));
            Assert.Equal(standIn, CrewReservationManager.CrewReplacements[Jeb]);
            Assert.False(module.ShouldFilterFromCrewDialog(standIn));
            Assert.True(module.ShouldFilterFromCrewDialog(Jeb));
        }

        // ------------------------------------------------------------------
        // Pure decisions
        // ------------------------------------------------------------------

        private static KerbalsModule.KerbalReservation Res(double until, bool permanent = false)
        {
            return new KerbalsModule.KerbalReservation
            {
                KerbalName = Jeb,
                ReservedUntilUT = until,
                IsPermanent = permanent
            };
        }

        [Fact]
        public void IsReservationActiveAt_BoundaryAndSpecialCases()
        {
            Assert.True(KerbalsModule.IsReservationActiveAt(Res(300.0), 299.999));
            Assert.False(KerbalsModule.IsReservationActiveAt(Res(300.0), 300.0));
            Assert.False(KerbalsModule.IsReservationActiveAt(Res(300.0), 301.0));
            Assert.True(KerbalsModule.IsReservationActiveAt(Res(300.0), double.NaN));
            Assert.True(KerbalsModule.IsReservationActiveAt(Res(double.PositiveInfinity), 1e12));
            Assert.True(KerbalsModule.IsReservationActiveAt(Res(300.0, permanent: true), 1e12));
            Assert.False(KerbalsModule.IsReservationActiveAt(null, 1.0));
        }

        [Fact]
        public void ResolveWalkClockUT_ResolutionOrder()
        {
            // OnLoad: the loaded save's clock, never the (stale) cutoff or live clock.
            Assert.Equal(200.0, KerbalsModule.ResolveWalkClockUT(900.0, true, 200.0, false, double.NaN, 900.0));
            Assert.True(double.IsNaN(KerbalsModule.ResolveWalkClockUT(900.0, true, 0.0, false, double.NaN, 900.0)));
            // The walk's cutoff wins over everything else outside OnLoad (0 is a real cutoff).
            Assert.Equal(150.0, KerbalsModule.ResolveWalkClockUT(150.0, false, double.NaN, true, 50.0, 900.0));
            Assert.Equal(0.0, KerbalsModule.ResolveWalkClockUT(0.0, false, double.NaN, false, double.NaN, 900.0));
            // The Re-Fly post-invoke "walk everything" sentinel is not a clock.
            Assert.Equal(13.9, KerbalsModule.ResolveWalkClockUT(double.MaxValue, false, double.NaN, false, double.NaN, 13.9));
            Assert.Equal(50.0, KerbalsModule.ResolveWalkClockUT(double.MaxValue, false, double.NaN, true, 50.0, 900.0));
            // A pending rewind UT adjustment: the adjusted UT, not the pre-rewind live clock.
            Assert.Equal(50.0, KerbalsModule.ResolveWalkClockUT(null, false, double.NaN, true, 50.0, 900.0));
            Assert.True(double.IsNaN(KerbalsModule.ResolveWalkClockUT(null, false, double.NaN, true, 0.0, 900.0)));
            // Otherwise the live clock; not-ready reads as unknown.
            Assert.Equal(900.0, KerbalsModule.ResolveWalkClockUT(null, false, double.NaN, false, double.NaN, 900.0));
            Assert.True(double.IsNaN(KerbalsModule.ResolveWalkClockUT(null, false, double.NaN, false, double.NaN, 0.0)));
            Assert.True(double.IsNaN(KerbalsModule.ResolveWalkClockUT(null, false, double.NaN, false, double.NaN, double.NaN)));
        }

        [Fact]
        public void IsReservationReleaseDue_Decisions()
        {
            Assert.False(KerbalsModule.IsReservationReleaseDue(299.0, 300.0, double.NaN));
            Assert.True(KerbalsModule.IsReservationReleaseDue(300.0, 300.0, double.NaN));
            Assert.True(KerbalsModule.IsReservationReleaseDue(500.0, 300.0, 250.0));
            Assert.False(KerbalsModule.IsReservationReleaseDue(500.0, 300.0, 300.0)); // already fired
            Assert.False(KerbalsModule.IsReservationReleaseDue(500.0, double.PositiveInfinity, double.NaN));
            Assert.False(KerbalsModule.IsReservationReleaseDue(0.0, 0.0, double.NaN));
            Assert.False(KerbalsModule.IsReservationReleaseDue(double.NaN, 300.0, double.NaN));
        }

        [Fact]
        public void ComputeNextReleaseUT_EarliestFiniteEndStillInForce()
        {
            var set = new[]
            {
                Res(300.0), Res(500.0), Res(100.0), Res(double.PositiveInfinity),
                Res(50.0, permanent: true)
            };
            Assert.Equal(300.0, KerbalsModule.ComputeNextReleaseUT(set, 200.0));
            Assert.Equal(100.0, KerbalsModule.ComputeNextReleaseUT(set, double.NaN));
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ComputeNextReleaseUT(set, 600.0)));
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ComputeNextReleaseUT(null, 1.0)));
        }

        [Fact]
        public void DescribeReservationTransition_OnlyRealTransitionsAreNamed()
        {
            Assert.Equal("released", KerbalsModule.DescribeReservationTransition(false, false, false));
            Assert.Equal("released", KerbalsModule.DescribeReservationTransition(true, true, false));
            Assert.Equal("re-reserved", KerbalsModule.DescribeReservationTransition(true, false, true));
            Assert.Null(KerbalsModule.DescribeReservationTransition(false, false, true));
            Assert.Null(KerbalsModule.DescribeReservationTransition(true, true, true));
            Assert.Null(KerbalsModule.DescribeReservationTransition(true, false, false));
        }

        private sealed class FakeRoster : KerbalsModule.IKerbalRosterFacade
        {
            private readonly Dictionary<string, ProtoCrewMember.RosterStatus> statuses =
                new Dictionary<string, ProtoCrewMember.RosterStatus>();
            private int generatedSeq;

            internal int CreatedCount { get; private set; }
            internal int RecreatedCount { get; private set; }
            internal int RemovedCount { get; private set; }

            internal void Add(string name, ProtoCrewMember.RosterStatus status)
            {
                statuses[name] = status;
            }

            internal bool Contains(string name)
            {
                return statuses.ContainsKey(name);
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                return statuses.TryGetValue(name, out status);
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedSeq++;
                generatedName = "StandIn " + generatedSeq + " Kerman";
                statuses[generatedName] = ProtoCrewMember.RosterStatus.Available;
                CreatedCount++;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                statuses[desiredName] = ProtoCrewMember.RosterStatus.Available;
                RecreatedCount++;
                return true;
            }

            public bool TryRemove(string name)
            {
                if (!statuses.Remove(name)) return false;
                RemovedCount++;
                return true;
            }

            public bool IsKerbalOnLiveVessel(string kerbalName)
            {
                return false;
            }

            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId)
            {
                return false;
            }

            public List<KerbalCareerLogEntry> GetCareerLogEntries(string kerbalName)
            {
                return null;
            }

            public int AppendCareerLogEntries(
                string kerbalName, IReadOnlyList<KerbalCareerLogEntry> entries)
            {
                return -1;
            }
        }
    }
}
