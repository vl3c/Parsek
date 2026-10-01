using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOOP-RECORDING-CREW-NEVER-RESERVED: the per-recording Loop toggle is visual only
    /// (design 12.7, operator ruling 2026-09-27). The first run of a looped recording is
    /// the real flight, so its crew are held exactly like a non-looping committed
    /// recording's for the span the flight occupied; the later loop replays are ghosts and
    /// hold nobody. Before the fix the kerbals walk skipped a looped recording's
    /// KerbalAssignment rows outright, so ticking Loop freed its crew for other flights
    /// across the recorded window (a double-booking hole).
    ///
    /// <para>Every cell drives the production recalculation
    /// (<see cref="LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT"/>, the same
    /// walk a rewind runs) over committed recordings and ledger rows.</para>
    /// </summary>
    [Collection("Sequential")]
    public class LoopRecordingCrewReservationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const double StartUT = 100.0;
        private const double EndUT = 200.0;

        public LoopRecordingCrewReservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
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

        private static KerbalsModule Kerbals => LedgerOrchestrator.Kerbals;

        private static Recording CommitFlight(
            string id, double startUT, double endUT, bool loop,
            string treeId = null, string parentBp = null, string childBp = null)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("crew", Jeb);
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Looper " + id,
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                CrewEndStatesResolved = true,
                LoopPlayback = loop,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static void AddAssignment(
            string recordingId, double startUT, double endUT, KerbalEndState endState, int sequence)
        {
            Ledger.AddAction(new GameAction
            {
                UT = startUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                StartUT = (float)startUT,
                EndUT = (float)endUT,
                KerbalEndStateField = endState,
                Sequence = sequence
            });
        }

        private static void Walk(double clockUT)
        {
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "loop-crew-test");
        }

        // catches: the walk skipping a looped recording's crew rows (the repro): a rewind
        // into the recorded window found Jeb free while the flight that carries him plays.
        [Fact]
        public void LoopedRecording_RecoveredCrew_HeldForTheRecordedFlightOnly()
        {
            var rec = CommitFlight("rec-loop", StartUT, EndUT, loop: true);
            AddAssignment(rec.RecordingId, StartUT, EndUT, KerbalEndState.Recovered, 1);

            Walk(150.0);
            Assert.True(Kerbals.Reservations.ContainsKey(Jeb), "a looped flight reserved none of its crew");
            Assert.Equal(EndUT, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.Reservations[Jeb].IsPermanent);
            Assert.True(Kerbals.IsReservedNow(Jeb));
            Assert.False(Kerbals.IsKerbalAvailable(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Loop recording holds crew like its real first run: 'Jebediah Kerman' recording 'rec-loop'"));

            // The loop replays after the first run are ghosts: they extend nothing.
            Walk(EndUT + 5000.0);
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.True(Kerbals.IsKerbalAvailable(Jeb));
        }

        // catches: the same skip for a flight that ended with the crew still aboard (a
        // landed, never-recovered vessel holds him open-ended, looped or not).
        [Fact]
        public void LoopedRecording_AboardCrew_HeldOpenEnded()
        {
            var rec = CommitFlight("rec-loop-landed", StartUT, EndUT, loop: true);
            AddAssignment(rec.RecordingId, StartUT, EndUT, KerbalEndState.Aboard, 1);

            Walk(1e6);
            Assert.True(Kerbals.Reservations.ContainsKey(Jeb));
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));
            Assert.True(Kerbals.IsManaged(Jeb));
        }

        // catches: the Loop toggle (either direction) moving a reservation at all. The same
        // committed flight with Loop off, on, then off again must hold Jeb identically.
        [Theory]
        [InlineData(KerbalEndState.Recovered, 150.0)]
        [InlineData(KerbalEndState.Aboard, 1e6)]
        [InlineData(KerbalEndState.Dead, 1e6)]
        [InlineData(KerbalEndState.Unknown, 1e6)]
        public void LoopToggle_OnThenOff_LeavesTheSameReservation(KerbalEndState endState, double clock)
        {
            var rec = CommitFlight("rec-toggle", StartUT, EndUT, loop: false);
            AddAssignment(rec.RecordingId, StartUT, EndUT, endState, 1);

            Walk(clock);
            var off = Snapshot();

            rec.LoopPlayback = true;
            Walk(clock);
            var on = Snapshot();

            rec.LoopPlayback = false;
            Walk(clock);
            var offAgain = Snapshot();

            Assert.True(off.Held, "baseline: a non-looping committed flight holds its crew");
            Assert.Equal(off, on);
            Assert.Equal(off, offAgain);
        }

        private struct HoldSnapshot
        {
            public bool Held;
            public double Until;
            public bool Permanent;
            public bool ReservedNow;
            public bool InAnyRecording;

            public override string ToString()
            {
                return "held=" + Held + " until=" + Until.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + " permanent=" + Permanent + " reservedNow=" + ReservedNow
                    + " inAnyRecording=" + InAnyRecording;
            }
        }

        private static HoldSnapshot Snapshot()
        {
            KerbalsModule.KerbalReservation r;
            bool held = Kerbals.Reservations.TryGetValue(Jeb, out r);
            return new HoldSnapshot
            {
                Held = held,
                Until = held ? r.ReservedUntilUT : double.NaN,
                Permanent = held && r.IsPermanent,
                ReservedNow = Kerbals.IsReservedNow(Jeb),
                InAnyRecording = Kerbals.IsKerbalInAnyRecording(Jeb)
            };
        }

        // catches: the stand-in side treating a kerbal who flew only a looped flight as
        // never used (a stand-in in that seat would be deleted instead of retired).
        [Fact]
        public void LoopedRecording_CrewCountsAsUsedInARecording()
        {
            var rec = CommitFlight("rec-loop-used", StartUT, EndUT, loop: true);
            AddAssignment(rec.RecordingId, StartUT, EndUT, KerbalEndState.Recovered, 1);

            Walk(150.0);
            Assert.True(Kerbals.IsKerbalInAnyRecording(Jeb));
        }

        // catches: the split handoff ignoring a looped child. The parent ended at the split
        // with Jeb Unknown; the looped child carries him on and recovers him at its end, so
        // the parent's hold must end at the split rather than stay open forever.
        [Fact]
        public void LoopedChild_EndsTheParentsHoldAtTheSplit()
        {
            var parent = CommitFlight("rec-parent", StartUT, 150.0, loop: false, childBp: "bp-split");
            CommitFlight("rec-child", 150.0, EndUT, loop: true,
                treeId: parent.TreeId, parentBp: "bp-split");
            AddAssignment("rec-parent", StartUT, 150.0, KerbalEndState.Unknown, 1);
            AddAssignment("rec-child", 150.0, EndUT, KerbalEndState.Recovered, 2);

            Walk(1e6);
            Assert.Equal(EndUT, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by split handoff: 'Jebediah Kerman' recording 'rec-parent'")
                && l.Contains("child='rec-child'"));
        }

        // catches: a recovery of the real vessel failing to close a looped recording's
        // open-ended hold (the walk side of CrewRecoveryReservationClose).
        [Fact]
        public void LoopedRecording_AboardHold_IsClosedByTheRecovery()
        {
            var rec = CommitFlight("rec-loop-recovered", StartUT, EndUT, loop: true);
            AddAssignment(rec.RecordingId, StartUT, EndUT, KerbalEndState.Aboard, 1);
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.KerbalRecovered,
                RecordingId = rec.RecordingId,
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                Sequence = 2
            });

            Walk(250.0);
            Assert.Equal(300.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.True(Kerbals.IsReservedNow(Jeb));

            Walk(400.0);
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }
    }
}
