using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KERBAL-XP-UNTAGGED-RECOVERY-HAS-NO-LEDGER-ROW.
    ///
    /// <para>
    /// The gap this closes: a recovery that reaches
    /// <c>GameStateRecorder.OnVesselRecoveryProcessingForExperience</c> with no live
    /// recorder emitted the <c>ExperienceGained</c> EVENT and deliberately wrote no
    /// ledger row, because a null-<c>RecordingId</c> <c>KerbalExperience</c> row could
    /// never be tombstoned. That is measurably the ORDINARY case, not just the
    /// tracking-station one: the recovery reward burst fires in SPACECENTER a few ms
    /// AFTER the scene-exit auto-commit has closed the tree (run
    /// <c>2026-08-19_2220_L3-career-science-recover</c> logged
    /// <c>untaggedNoLedgerRow=1</c> and zero <c>KerbalExperience</c> rows).
    /// </para>
    ///
    /// <para>
    /// The fix is the correlation:
    /// <c>LedgerOrchestrator.TryRecordRecoveryKerbalExperience</c> resolves an owner
    /// through <c>PickRecoveryRecordingId</c> - the SAME function the recovery funds and
    /// science legs use, which is load-bearing because
    /// <c>ResurrectionRetirementEligibility</c> retires those rows as one same-recording
    /// bundle - and REFUSES the write when no owner resolves.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class LedgerRecoveryKerbalExperienceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerRecoveryKerbalExperienceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----------------------------------------------------------------
        // Fixture helpers
        // ----------------------------------------------------------------

        private static string Entries(int flight, params string[] typeBodyPairs)
        {
            var list = new List<KerbalCareerLogEntry>();
            for (int i = 0; i + 1 < typeBodyPairs.Length; i += 2)
                list.Add(new KerbalCareerLogEntry(flight, typeBodyPairs[i], typeBodyPairs[i + 1]));
            return KerbalCareerLogEntry.FormatSet(list);
        }

        private static GameStateEvent XpEvent(string kerbal, string encoded, double ut, string trait = "Pilot")
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ExperienceGained,
                key = kerbal,
                detail = $"flight=1;entries={encoded}" +
                         (string.IsNullOrEmpty(trait) ? "" : $";trait={trait}")
            };
        }

        /// <summary>
        /// A committed recording named <paramref name="vesselName"/> that ENDED before the
        /// recovery UT - the tier the live seam actually measured (`tier=most-recent-ended`).
        /// </summary>
        private static void AddEndedRecording(string id, string vesselName, double startUt, double endUt)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = endUt, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);
        }

        // ----------------------------------------------------------------
        // The forward gate
        // ----------------------------------------------------------------

        [Fact]
        public void Gate_ForwardsOnlyWhenUntaggedAndNothingCanStillClaimIt()
        {
            // Untagged, no live recorder, no uncommitted tree: nothing else will ever
            // convert this event, so this handler owns it. THIS is the measured
            // post-auto-commit recovery case.
            Assert.True(GameStateRecorder.ShouldForwardDirectScienceSubject("", false, false));

            // Tagged: a live recorder owns the event and the commit-time ConvertEvents
            // path writes its row. Forwarding here would double-count.
            Assert.False(GameStateRecorder.ShouldForwardDirectScienceSubject("rec-1", true, false));

            // Empty tag WHILE a recorder is live is tag drift, not proof of ownerlessness.
            Assert.False(GameStateRecorder.ShouldForwardDirectScienceSubject("", true, false));

            // An active uncommitted tree can still claim the event at commit time.
            Assert.False(GameStateRecorder.ShouldForwardDirectScienceSubject("", false, true));
        }

        // ----------------------------------------------------------------
        // The write
        // ----------------------------------------------------------------

        [Fact]
        public void Forward_WritesAScopedRowCorrelatedToTheRecoveredRecording()
        {
            AddEndedRecording("rec-flea", "Jumping Flea", 100.0, 300.0);

            string encoded = Entries(1, "Flight", "Kerbin", "Suborbit", "Kerbin");
            int covered = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", encoded, 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            Assert.Equal(1, covered);

            var rows = Ledger.Actions
                .Where(a => a.Type == GameActionType.KerbalExperience)
                .ToList();
            Assert.Single(rows);
            Assert.Equal("rec-flea", rows[0].RecordingId);
            Assert.Equal("Jebediah Kerman", rows[0].KerbalName);
            Assert.Equal("Pilot", rows[0].KerbalRole);
            Assert.Equal(encoded, rows[0].KerbalCareerEntries);
            Assert.Equal(347.5, rows[0].UT, 3);

            // The house rule: the write is logged, grep-stably.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Recovery kerbal XP recorded:") &&
                l.Contains("recordingId=rec-flea") &&
                l.Contains("rows=1"));
        }

        [Fact]
        public void Forward_ScopesTheRowToTheSameRecordingTheRecoveryFundsRowUses()
        {
            // The bundle argument, asserted rather than described: whatever
            // PickRecoveryRecordingId hands the funds leg is what the XP row must carry,
            // or ResurrectionRetirementEligibility retires half a recovery.
            AddEndedRecording("rec-old", "Jumping Flea", 100.0, 200.0);
            AddEndedRecording("rec-new", "Jumping Flea", 250.0, 300.0);

            string fundsScope = LedgerOrchestrator.PickRecoveryRecordingId("Jumping Flea", 347.5);
            Assert.Equal("rec-new", fundsScope);

            LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", Entries(1, "Flight", "Kerbin"), 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            var row = Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience);
            Assert.Equal(fundsScope, row.RecordingId);
        }

        [Fact]
        public void Forward_TwoCrewProduceTwoRowsAtTheSameUt()
        {
            // LANDMINE: every crew member's row shares the recovery's UT exactly (one
            // ArchiveFlightLog pass, one Planetarium read). Before KerbalExperience got a
            // GetActionKey case, Type + UT + "" matched them against each other and the
            // second kerbal's XP silently vanished.
            AddEndedRecording("rec-flea", "Jumping Flea", 100.0, 300.0);

            int covered = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent>
                {
                    XpEvent("Jebediah Kerman", Entries(1, "Flight", "Kerbin"), 347.5),
                    XpEvent("Bill Kerman", Entries(1, "Flight", "Kerbin"), 347.5, "Engineer"),
                },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            Assert.Equal(2, covered);

            var rows = Ledger.Actions
                .Where(a => a.Type == GameActionType.KerbalExperience)
                .ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.KerbalName == "Jebediah Kerman" && r.KerbalRole == "Pilot");
            Assert.Contains(rows, r => r.KerbalName == "Bill Kerman" && r.KerbalRole == "Engineer");
            Assert.All(rows, r => Assert.Equal("rec-flea", r.RecordingId));
            // Distinct sequences: two rows, not one row written twice.
            Assert.Equal(2, rows.Select(r => r.Sequence).Distinct().Count());
        }

        [Fact]
        public void GetActionKey_KerbalExperienceIsKeyedByKerbalName()
        {
            var jeb = new GameAction
            {
                Type = GameActionType.KerbalExperience,
                KerbalName = "Jebediah Kerman",
                RecordingId = "rec-flea"
            };
            var bill = new GameAction
            {
                Type = GameActionType.KerbalExperience,
                KerbalName = "Bill Kerman",
                RecordingId = "rec-flea"
            };

            Assert.Equal("Jebediah Kerman", LedgerOrchestrator.GetActionKey(jeb));
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(jeb), LedgerOrchestrator.GetActionKey(bill));
        }

        // ----------------------------------------------------------------
        // The refusals - the carve-out's fail-safe, kept
        // ----------------------------------------------------------------

        [Fact]
        public void Forward_RefusesWhenNoRecordingCorrelates()
        {
            // No recording of that name at all: the pick returns null and the write is
            // REFUSED rather than falling back to a null-scoped row that no merge could
            // ever tombstone. That refusal is the whole reason the original carve-out
            // existed and it survives this fix intact.
            AddEndedRecording("rec-other", "Some Other Craft", 100.0, 300.0);

            int covered = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", Entries(1, "Flight", "Kerbin"), 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            Assert.Equal(0, covered);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalExperience);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Recovery kerbal XP refused:") &&
                l.Contains("reason=no-recovery-recording"));
        }

        [Fact]
        public void Forward_RefusesAnEventWithNoCareerEntries()
        {
            // An XP row with an empty entry set has nothing to re-assert; the converter
            // returns null and the forward must not write an inert row.
            AddEndedRecording("rec-flea", "Jumping Flea", 100.0, 300.0);

            int covered = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", "", 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            Assert.Equal(0, covered);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalExperience);
            Assert.Contains(logLines, l =>
                l.Contains("Recovery kerbal XP refused:") &&
                l.Contains("reason=no-convertible-row"));
        }

        [Fact]
        public void Forward_EmptyBatchIsANoOp()
        {
            Assert.Equal(0, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent>(),
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5));
            Assert.Empty(Ledger.Actions);
        }

        [Fact]
        public void Forward_IsIdempotentAcrossARepeatedBurst()
        {
            // A stock re-fire of the same recovery must not double the XP. The second
            // pass dedups against the ledger and reports the rows as covered, so the
            // caller's untaggedNoLedgerRow counter does not falsely re-open the gap.
            AddEndedRecording("rec-flea", "Jumping Flea", 100.0, 300.0);

            var batch = new List<GameStateEvent>
            {
                XpEvent("Jebediah Kerman", Entries(1, "Flight", "Kerbin"), 347.5),
                XpEvent("Bill Kerman", Entries(1, "Flight", "Kerbin"), 347.5, "Engineer"),
            };
            var identity = RecoveredVesselIdentity.FromRawName("Jumping Flea");

            Assert.Equal(2, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(batch, identity, 347.5));
            Assert.Equal(2, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(batch, identity, 347.5));

            Assert.Equal(2, Ledger.Actions.Count(a => a.Type == GameActionType.KerbalExperience));
        }

        // ----------------------------------------------------------------
        // Downstream: the row the forward writes is the row the merge can retire
        // ----------------------------------------------------------------

        [Fact]
        public void ForwardedRow_IsScopedAndThereforeTombstoneEligible()
        {
            AddEndedRecording("rec-flea", "Jumping Flea", 100.0, 300.0);

            LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", Entries(1, "Flight", "Kerbin"), 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea"),
                347.5);

            var row = Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience);
            Assert.False(string.IsNullOrEmpty(row.RecordingId));
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(row));
        }
    }
}
