using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Does a crew reservation from a committed flight LIFT once game time passes the
    /// flight's recorded end?
    ///
    /// <para>The question the 2026-09-22 Kerbals-window review left open: the window
    /// printed <c>Reserved until &lt;date&gt;</c> off <c>KerbalReservation.ReservedUntilUT</c>
    /// (the flight's EndUT for a Recovered end state), but nothing in the backend reads
    /// that field back. These cells drive the REAL production walk -
    /// <see cref="LedgerOrchestrator.RecalculateAndPatch"/> with the current-UT cutoff the
    /// live clock paths pass, plus the uncut full replay - with the clock before the
    /// flight's launch, during it, at its end and long after, and read what the rest of
    /// Parsek reads: the reservation map, <see cref="KerbalsModule.IsKerbalAvailable"/>
    /// and <see cref="KerbalsModule.ShouldFilterFromCrewDialog"/> (the VAB/SPH crew
    /// dialog filter).</para>
    ///
    /// <para>ANSWER, pinned here: it does not lift. The reservation is present at every
    /// clock position (a cutoff walk re-derives reservations from the whole effective
    /// ledger - <c>CrewReservationManager.RecomputeAfterCutoffWalk</c>), with
    /// <c>ReservedUntilUT</c> still equal to the flight's end, and the kerbal stays
    /// filtered from the crew dialog. The only thing measured to release it is the flight
    /// leaving the committed set. The window's wording follows this
    /// (<see cref="KerbalsPresentation.ReservationHoldRule"/>); whether the backend SHOULD
    /// release a recovered kerbal at the flight's end is filed for the owner in
    /// <c>docs/dev/todo-and-known-bugs.md</c> (reservation semantics unchanged here).</para>
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
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void CommitFlight(KerbalEndState endState)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("crew", Jeb);
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = RecordingId,
                VesselName = "Jumping Flea",
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = StartUT,
                ExplicitEndUT = EndUT,
                CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, endState } },
                CrewEndStatesResolved = true
            });

            // The row LedgerOrchestrator.CreateKerbalAssignmentActions writes: stamped at
            // the flight's START, carrying its end state.
            Ledger.AddAction(new GameAction
            {
                UT = StartUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = RecordingId,
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                StartUT = (float)StartUT,
                EndUT = (float)EndUT,
                KerbalEndStateField = endState,
                Sequence = 1
            });
        }

        private static void AssertHeld(string clock, double expectedUntilUT)
        {
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.NotNull(kerbals);
            Assert.True(kerbals.Reservations.ContainsKey(Jeb),
                "reservation missing at clock " + clock);
            Assert.Equal(expectedUntilUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
            Assert.False(kerbals.IsKerbalAvailable(Jeb),
                "kerbal reads available at clock " + clock);
            Assert.True(kerbals.ShouldFilterFromCrewDialog(Jeb),
                "kerbal not filtered from the crew dialog at clock " + clock);
        }

        [Theory]
        [InlineData(50.0)]    // before the flight launched
        [InlineData(200.0)]   // mid-flight
        [InlineData(300.0)]   // exactly the recorded end (the old "until" date)
        [InlineData(10000.0)] // long after it
        public void RecoveredFlight_ReservationDoesNotLift_AtAnyCurrentUT(double currentUT)
        {
            CommitFlight(KerbalEndState.Recovered);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(currentUT, "release-test");

            AssertHeld(currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                expectedUntilUT: EndUT);
        }

        [Fact]
        public void RecoveredFlight_ReservationDoesNotLift_OnTheFullReplay()
        {
            CommitFlight(KerbalEndState.Recovered);

            LedgerOrchestrator.RecalculateAndPatch();

            AssertHeld("null (full replay)", expectedUntilUT: EndUT);
        }

        [Theory]
        [InlineData(200.0)]
        [InlineData(10000.0)]
        public void AboardFlight_ReservationIsOpenEnded_AndDoesNotLift(double currentUT)
        {
            CommitFlight(KerbalEndState.Aboard);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(currentUT, "release-test");

            AssertHeld(currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                expectedUntilUT: double.PositiveInfinity);
        }

        [Fact]
        public void Reservation_Lifts_WhenTheFlightLeavesTheCommittedSet()
        {
            CommitFlight(KerbalEndState.Recovered);
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(10000.0, "release-test");
            AssertHeld("10000 (before removal)", expectedUntilUT: EndUT);

            Assert.True(RecordingStore.RemoveCommittedById(RecordingId));
            LedgerOrchestrator.RecalculateAndPatch();

            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.False(kerbals.Reservations.ContainsKey(Jeb));
            Assert.True(kerbals.IsKerbalAvailable(Jeb));
            Assert.False(kerbals.ShouldFilterFromCrewDialog(Jeb));
        }
    }
}
