using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression suite for the crew-end-state latch: a recording whose end states
    /// were inferred BEFORE it had a terminal verdict (the re-fly fork, which receives
    /// its VesselSnapshot at rewind time while <c>TerminalStateValue</c> is still null)
    /// used to keep every crew member at <c>KerbalEndState.Unknown</c> forever, because
    /// <c>PopulateCrewEndStates</c> latches BOTH <c>CrewEndStates</c> and the serialized
    /// <c>CrewEndStatesResolved</c> flag and <c>NeedsCrewEndStatePopulation</c> treats
    /// either as a permanent skip.
    ///
    /// Benign for a fork ending Landed/Aboard (identical reservations); NOT benign for
    /// a fork ending <c>Recovered</c> — the only end state carrying a finite endUT —
    /// where the kerbal stayed reserved forever (the D12 missed-endut-auto-free surface).
    ///
    /// Everything here drives the production path:
    /// <c>Recording.StampTerminalState</c> -&gt;
    /// <c>KerbalsModule.InvalidateCrewEndStatesForTerminalStamp</c> (which re-infers
    /// immediately, against the surface its admission guard judged) -&gt;
    /// <c>LedgerOrchestrator.CreateKerbalAssignmentActions</c> (which consults
    /// <c>NeedsCrewEndStatePopulation</c>) -&gt; the <c>KerbalsModule</c> walk.
    /// </summary>
    [Collection("Sequential")] // KerbalsModule / RecordingStore / crewReplacements statics
    public class CrewEndStateTerminalStampInvalidationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewEndStateTerminalStampInvalidationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RecalculationEngine.ClearModules();
            LedgerOrchestrator.SetKerbalsForTesting(new KerbalsModule());
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetKerbalsForTesting(new KerbalsModule());
            RecalculationEngine.ClearModules();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ========================================================
        // Fixture helpers
        // ========================================================

        private const string InvalidationKey =
            KerbalsModule.CrewEndStateStaleAfterTerminalStampKey;

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        /// <summary>
        /// The re-fly fork shape: both snapshots present (RewindInvoker refreshes them
        /// from the live vessel at rewind time) and NO terminal verdict yet.
        /// </summary>
        private static Recording AddFork(
            string id,
            string[] crew,
            TerminalState? terminal = null,
            double startUT = 100.0,
            double endUT = 5000.0)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                GhostVisualSnapshot = CrewSnapshot(crew),
                VesselSnapshot = CrewSnapshot(crew),
                TerminalStateValue = terminal,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        /// <summary>
        /// One "recalc" over a single recording: the production commit/migration entry
        /// point, which populates end states when admitted and emits the ledger rows.
        /// </summary>
        private static List<GameAction> Recalc(Recording rec)
        {
            return LedgerOrchestrator.CreateKerbalAssignmentActions(
                rec.RecordingId, rec.StartUT, rec.EndUT);
        }

        private static KerbalsModule Walk(List<GameAction> actions)
        {
            var module = new KerbalsModule();
            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
        }

        private bool SawInvalidation()
        {
            return logLines.Any(l => l.Contains(InvalidationKey) && l.Contains("dropped"));
        }

        private void ClearLog() => logLines.Clear();

        // ========================================================
        // The latch lifecycle
        // ========================================================

        [Fact]
        public void ForkLatchedUnknown_ThenStampedRecovered_ReInfersAndTheKerbalFrees()
        {
            const double endUT = 5000.0;
            var fork = AddFork("refly-fork", new[] { "Jebediah Kerman" }, terminal: null, endUT: endUT);

            // 1. The rewind-time recalc: no terminal yet, so every crew member infers
            //    Unknown and BOTH latches close.
            var latched = Recalc(fork);
            Assert.Single(latched);
            Assert.Equal(KerbalEndState.Unknown, latched[0].KerbalEndStateField);
            Assert.True(fork.CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Unknown, fork.CrewEndStates["Jebediah Kerman"]);
            Assert.False(LedgerOrchestrator.NeedsCrewEndStatePopulation(fork));

            // Pre-fix, a second recalc could never revisit the answer.
            Assert.Equal(
                KerbalEndState.Unknown,
                Recalc(fork).Single().KerbalEndStateField);

            // 2. The fork ends: its terminal verdict is stamped through the seam, which
            //    retires the stale answer and re-infers against the real terminal.
            ClearLog();
            bool invalidated = fork.StampTerminalState(TerminalState.Recovered, "test-refly-end");

            Assert.True(invalidated);
            Assert.Equal(KerbalEndState.Recovered, fork.CrewEndStates["Jebediah Kerman"]);
            Assert.True(fork.CrewEndStatesResolved);
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains(InvalidationKey)
                && l.Contains("refly-fork")
                && l.Contains("null->Recovered")
                && l.Contains("dropped 1 end state(s)"));

            // 3. The next recalc reads the corrected answer (and does not re-run).
            Assert.False(LedgerOrchestrator.NeedsCrewEndStatePopulation(fork));
            var reinferred = Recalc(fork);
            Assert.Single(reinferred);
            Assert.Equal(KerbalEndState.Recovered, reinferred[0].KerbalEndStateField);
            Assert.Equal(KerbalEndState.Recovered, fork.CrewEndStates["Jebediah Kerman"]);

            // 4. …and the reservation now carries a FINITE endUT, so the kerbal frees.
            var kerbals = Walk(reinferred);
            var reservation = kerbals.Reservations["Jebediah Kerman"];
            Assert.False(reservation.IsPermanent);
            Assert.Equal(endUT, reservation.ReservedUntilUT);
            Assert.False(double.IsInfinity(reservation.ReservedUntilUT));
        }

        [Fact]
        public void LatchedUnknown_WithoutTheStamp_StaysReservedForever()
        {
            // Negative control for the cell above: the latched state is what produced
            // the leak, and it is still exactly reproducible when no terminal is stamped.
            var fork = AddFork("refly-fork-nostamp", new[] { "Jebediah Kerman" });

            var kerbals = Walk(Recalc(fork));

            Assert.Equal(KerbalEndState.Unknown, fork.CrewEndStates["Jebediah Kerman"]);
            Assert.Equal(
                double.PositiveInfinity,
                kerbals.Reservations["Jebediah Kerman"].ReservedUntilUT);
        }

        [Fact]
        public void RawFieldAssignmentBypassingTheSeam_ReproducesTheLatch()
        {
            // Pins WHAT fixes the leak. Assigning the field directly — what every stamp
            // site did before this seam existed — leaves both latches closed, so the
            // recording keeps its rewind-time Unknown and the reservation stays infinite
            // even though the fork ended Recovered.
            var fork = AddFork("refly-fork-raw", new[] { "Jebediah Kerman" });

            Assert.Equal(KerbalEndState.Unknown, Recalc(fork).Single().KerbalEndStateField);

            fork.TerminalStateValue = TerminalState.Recovered; // deliberately NOT the seam

            var rows = Recalc(fork);
            Assert.Equal(KerbalEndState.Unknown, rows.Single().KerbalEndStateField);
            Assert.Equal(
                double.PositiveInfinity,
                Walk(rows).Reservations["Jebediah Kerman"].ReservedUntilUT);
        }

        [Fact]
        public void ForkLatchedUnknown_ThenStampedLanded_StaysBenign()
        {
            var fork = AddFork("refly-fork-landed", new[] { "Bill Kerman" });

            Assert.Equal(KerbalEndState.Unknown, Recalc(fork).Single().KerbalEndStateField);

            Assert.True(fork.StampTerminalState(TerminalState.Landed, "test-landed"));

            var rows = Recalc(fork);
            // The label changes (Unknown -> Aboard); the reservation does not.
            Assert.Equal(KerbalEndState.Aboard, rows.Single().KerbalEndStateField);

            var reservation = Walk(rows).Reservations["Bill Kerman"];
            Assert.False(reservation.IsPermanent);
            Assert.Equal(double.PositiveInfinity, reservation.ReservedUntilUT);
        }

        // ========================================================
        // The X -> Y re-stamp (deliberately in scope; see the seam's doc comment)
        // ========================================================

        [Fact]
        public void EndStatesInferredAgainstLanded_ThenRecoveredRestamp_ReInfersAndFrees()
        {
            // ParsekScenario.CanOverwriteTerminalState deliberately allows a
            // situation-based verdict to be overwritten by Recovered, which is the same
            // finite-endUT leak arrived at from a second direction.
            const double endUT = 4242.0;
            var rec = AddFork("landed-then-recovered", new[] { "Val Kerman" },
                terminal: TerminalState.Landed, endUT: endUT);

            Assert.Equal(KerbalEndState.Aboard, Recalc(rec).Single().KerbalEndStateField);

            ClearLog();
            // Mirrors the production ordering at that call site: the end snapshot is
            // dropped first, so the ghost snapshot is the surviving crew source.
            rec.VesselSnapshot = null;
            Assert.True(rec.StampTerminalState(TerminalState.Recovered, "test-ksc-recovery"));
            Assert.Contains(logLines, l =>
                l.Contains(InvalidationKey) && l.Contains("Landed->Recovered"));

            var rows = Recalc(rec);
            Assert.Equal(KerbalEndState.Recovered, rows.Single().KerbalEndStateField);
            Assert.Equal(endUT, Walk(rows).Reservations["Val Kerman"].ReservedUntilUT);
        }

        [Fact]
        public void EndStatesInferredAgainstLanded_ThenDestroyedOverride_ReInfersDead()
        {
            var rec = AddFork("landed-then-destroyed", new[] { "Bob Kerman" },
                terminal: TerminalState.Landed);

            Assert.Equal(KerbalEndState.Aboard, Recalc(rec).Single().KerbalEndStateField);

            Assert.True(rec.StampTerminalState(TerminalState.Destroyed, "test-destruction-override"));

            var rows = Recalc(rec);
            Assert.Equal(KerbalEndState.Dead, rows.Single().KerbalEndStateField);
            Assert.True(Walk(rows).Reservations["Bob Kerman"].IsPermanent);
        }

        // ========================================================
        // No-clear cases
        // ========================================================

        [Fact]
        public void RestampingTheSameTerminal_DoesNotClear()
        {
            var rec = AddFork("same-terminal", new[] { "Val Kerman" },
                terminal: TerminalState.Landed);

            Recalc(rec);
            var populated = rec.CrewEndStates;
            ClearLog();

            Assert.False(rec.StampTerminalState(TerminalState.Landed, "test-same"));

            Assert.Same(populated, rec.CrewEndStates);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.DoesNotContain(logLines, l => l.Contains(InvalidationKey));
        }

        [Fact]
        public void RecordingWithNoCrewEndStates_StampIsANoOp()
        {
            // Crewless recording: PopulateCrewEndStates resolves it with a null
            // dictionary, and re-running it could only produce the same answer.
            var rec = new Recording
            {
                RecordingId = "crewless",
                VesselName = "Probe",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                VesselSnapshot = new ConfigNode("VESSEL"),
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 90.0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.Empty(Recalc(rec));
            Assert.True(rec.CrewEndStatesResolved);
            Assert.Null(rec.CrewEndStates);
            ClearLog();

            Assert.False(rec.StampTerminalState(TerminalState.Recovered, "test-crewless"));

            Assert.True(rec.CrewEndStatesResolved);
            Assert.DoesNotContain(logLines, l => l.Contains(InvalidationKey));
        }

        [Fact]
        public void StampingNullTerminal_KeepsTheExistingEndStates()
        {
            // Retraction (post-spawn revert/rewind clears, the optimizer's split HEAD):
            // a null terminal could only ever re-infer to Unknown, so the seam keeps the
            // answer derived from the real flight.
            var rec = AddFork("retraction", new[] { "Val Kerman" },
                terminal: TerminalState.Recovered);

            Recalc(rec);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Val Kerman"]);
            ClearLog();

            Assert.False(rec.StampTerminalState(null, "test-retraction"));

            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Val Kerman"]);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.DoesNotContain(logLines, l => l.Contains(InvalidationKey));
        }

        [Fact]
        public void EndStatesThatCouldNotBeReDerived_AreKeptNotDropped()
        {
            // No ghost snapshot and no EVA crew name: PopulateCrewEndStates has no
            // start crew source and would leave the recording permanently unresolved,
            // so dropping the existing answer would read as crewless everywhere.
            var rec = new Recording
            {
                RecordingId = "no-start-crew-source",
                VesselName = "Orphan",
                VesselSnapshot = CrewSnapshot("Jebediah Kerman"),
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 900.0,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Aboard }
                },
                CrewEndStatesResolved = true,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            ClearLog();

            Assert.False(rec.StampTerminalState(TerminalState.Recovered, "test-not-rederivable"));

            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Jebediah Kerman"]);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.False(SawInvalidation());
            Assert.Contains(logLines, l =>
                l.Contains(InvalidationKey) && l.Contains("not re-derivable"));
        }

        [Fact]
        public void GhostOnlyConversionAfterTheStamp_CannotStrandTheRecordingUnresolved()
        {
            // Several commit paths null VesselSnapshot a few steps AFTER the terminal is
            // stamped (ghost-only conversion). Re-inference happens inside the stamp,
            // against the surface the guard judged, so the later nulling cannot leave the
            // recording permanently unresolved — which it would if the work were deferred
            // to a recalc that no longer admits it (SubOrbital is outside the
            // ghost-visual-only admission set).
            var rec = AddFork("ghost-only-after-stamp", new[] { "Val Kerman" });

            Assert.Equal(KerbalEndState.Unknown, Recalc(rec).Single().KerbalEndStateField);

            Assert.True(rec.StampTerminalState(TerminalState.SubOrbital, "test-scene-exit"));
            rec.VesselSnapshot = null; // ghost-only conversion, after the stamp

            Assert.True(rec.CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Val Kerman"]);
            Assert.False(LedgerOrchestrator.NeedsCrewEndStatePopulation(rec));
            Assert.Equal(KerbalEndState.Aboard, Recalc(rec).Single().KerbalEndStateField);
        }

        [Fact]
        public void MarkDestroyedAtTerminal_RoutesThroughTheSeam()
        {
            var rec = AddFork("mark-destroyed", new[] { "Jebediah Kerman" },
                terminal: TerminalState.Landed);

            Assert.Equal(KerbalEndState.Aboard, Recalc(rec).Single().KerbalEndStateField);
            ClearLog();

            rec.MarkDestroyedAtTerminal(1234.0, "test-source");

            Assert.True(SawInvalidation());
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Jebediah Kerman"]);
            Assert.Equal(KerbalEndState.Dead, Recalc(rec).Single().KerbalEndStateField);
        }
    }
}
