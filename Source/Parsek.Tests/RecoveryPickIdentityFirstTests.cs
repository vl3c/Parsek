using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY, STAGE 3: the recovery correlator is
    /// IDENTITY FIRST (operator principle 2026-10-01, "we should have unique identities").
    ///
    /// <para>
    /// <c>LedgerOrchestrator.PickRecoveryRecording</c> chooses the candidate set by the
    /// recovering vessel's launch guid first (names ignored), by a genuine Parsek spawn pid
    /// second, and by vessel NAME (plus the stage-1 guid filter and, on the XP leg, the
    /// stage-2 ambiguity refusal) only when neither identity reaches any recording. The UT
    /// tiers then pick the segment exactly as before.
    /// </para>
    ///
    /// <para>
    /// The cells drive the three production recovery legs past their KSP seams: the funds
    /// leg through <c>LedgerOrchestrator.OnVesselRecoveryFunds</c> with a paired
    /// <c>FundsChanged(VesselRecovery)</c> event, the science leg through
    /// <c>TryRecordKscScienceSubject</c> with the <c>VesselRecovery</c> reason, and the XP
    /// leg through <c>TryRecordRecoveryKerbalExperience</c> - each with the
    /// <see cref="RecoveredVesselIdentity"/> its seam builds from the <c>ProtoVessel</c>
    /// (name, <c>vesselID</c>, <c>persistentId</c>).
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryPickIdentityFirstTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbb4bbbbbbbbbbbbbbbbbbb";
        private const string GuidSpawn = "cccccccccccc4ccccccccccccccccccc";
        private const string Subject = "crewReport@KerbinSrfLandedLaunchPad";

        private readonly List<string> logLines = new List<string>();

        public RecoveryPickIdentityFirstTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

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

        private static Recording AddRec(
            string id, string vesselName, double startUt, double endUt, string launchGuid,
            TerminalState terminal = TerminalState.Landed)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                PreLaunchFunds = 50000.0,
                RecordedVesselGuid = launchGuid,
                VesselPersistentId = 2905720181u,
                TerminalStateValue = terminal
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = endUt, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static GameStateEvent XpEvent(string kerbal, double ut)
        {
            var entries = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(1, "Recover", "Kerbin")
            };
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ExperienceGained,
                key = kerbal,
                detail = $"flight=1;entries={KerbalCareerLogEntry.FormatSet(entries)};trait=Pilot"
            };
        }

        /// <summary>The funds leg: a paired FundsChanged(VesselRecovery) event, then the seam call.</summary>
        private static string RecoverFunds(RecoveredVesselIdentity identity, double ut)
        {
            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 40000.0,
                valueAfter = 44000.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnVesselRecoveryFunds(ut, identity, fromTrackingStation: true);
            var row = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.Recovery);
            return row.RecordingId;
        }

        /// <summary>The science leg: a VesselRecovery subject routed past the ProtoVessel seam.</summary>
        private static string RecoverScience(RecoveredVesselIdentity identity, double ut)
        {
            bool handled = LedgerOrchestrator.TryRecordKscScienceSubject(
                new PendingScienceSubject
                {
                    subjectId = Subject,
                    science = 1.5f,
                    subjectMaxValue = 5f,
                    captureUT = ut,
                    reasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                    recordingId = ""
                },
                identity.RawName, identity.LaunchGuid, identity.PersistentId);
            Assert.True(handled);
            var row = Ledger.Actions.Single(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == Subject);
            return row.RecordingId;
        }

        /// <summary>The XP leg. Returns the row's recording id, or null when it refused.</summary>
        private static string RecoverXp(RecoveredVesselIdentity identity, double ut)
        {
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", ut) }, identity, ut);
            if (rows == 0) return null;
            return Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience).RecordingId;
        }

        // ----------------------------------------------------------------
        // Renamed vessel
        // ----------------------------------------------------------------

        [Fact]
        public void RenamedVessel_CreditsItsOwnRecording_OnAllThreeLegs()
        {
            // THE DEFECT ON MAIN. The flight recorded as "Hopper" (two chained segments of
            // launch A) and the player renamed the vessel "Hopper Mk2" before recovering it.
            // A name-first correlator finds no candidate at all, so the funds and science rows
            // land untagged and the XP row is refused.
            AddRec("rec-seg-1", "Hopper", 100.0, 400.0, GuidA);
            AddRec("rec-seg-2", "Hopper", 400.0, 900.0, GuidA);
            var identity = RecoveredVesselIdentity.FromRawName("Hopper Mk2", GuidA, 2905720181u);

            Assert.Equal("rec-seg-2", RecoverFunds(identity, 1000.0));
            Assert.Equal("rec-seg-2", RecoverScience(identity, 1000.0));
            Assert.Equal("rec-seg-2", RecoverXp(identity, 1000.0));

            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId path: vessel='Hopper Mk2'")
                && l.Contains("path=launch-guid")
                && l.Contains("identityMatches=2")
                && l.Contains("identityNameMismatch=2")
                && l.Contains("nameOnlyIgnored=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("Recovery kerbal XP refused"));

            // Negative control: the same recovery with no identity is the name-first
            // correlator, and it finds nothing - the outcome every leg had before stage 3.
            Assert.Null(LedgerOrchestrator.PickRecoveryRecordingId("Hopper Mk2", 1000.0));
        }

        [Fact]
        public void RenamedVessel_TakingAnOlderLaunchesName_CreditsItsOwnLaunch()
        {
            // The rename lands on the name of a DIFFERENT, earlier launch. Name-first plus
            // the stage-1 filter matched only the other launch, dropped it, and credited
            // nothing; identity-first credits the vessel's own recording and never looks at
            // the namesake.
            AddRec("rec-namesake", "Hopper", 100.0, 300.0, GuidB);
            AddRec("rec-mine", "Probe One", 500.0, 900.0, GuidA);
            var identity = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 2905720181u);

            Assert.Equal("rec-mine", RecoverFunds(identity, 1000.0));
            Assert.Equal("rec-mine", RecoverXp(identity, 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("path=launch-guid")
                && l.Contains("identityMatches=1")
                && l.Contains("identityNameMismatch=1")
                && l.Contains("nameOnlyIgnored=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("PickRecoveryRecordingId guid filter"));
        }

        // ----------------------------------------------------------------
        // Two same-name launches
        // ----------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TwoSameNameLaunches_CreditTheRecoveredLaunch_RegardlessOfUtOrder(bool recoveredIsLater)
        {
            // Launch A and launch B share the craft name and the craft-baked pid. Whichever
            // ordering the two flights have, and even when the OTHER launch still brackets the
            // recovery moment (the shape tier 1 used to hand to the wrong launch), the
            // recovery credits the launch whose guid it carries.
            string recoveredGuid = recoveredIsLater ? GuidB : GuidA;
            string expected = recoveredIsLater ? "rec-launch-B" : "rec-launch-A";
            if (recoveredIsLater)
            {
                AddRec("rec-launch-A", "Hopper", 100.0, 5000.0, GuidA, TerminalState.Orbiting);
                AddRec("rec-launch-B", "Hopper", 950.0, 990.0, GuidB);
            }
            else
            {
                AddRec("rec-launch-A", "Hopper", 100.0, 400.0, GuidA);
                AddRec("rec-launch-B", "Hopper", 500.0, 5000.0, GuidB, TerminalState.Orbiting);
            }
            var identity = RecoveredVesselIdentity.FromRawName("Hopper", recoveredGuid, 2905720181u);

            Assert.Equal(expected, RecoverFunds(identity, 1000.0));
            Assert.Equal(expected, RecoverScience(identity, 1000.0));
            Assert.Equal(expected, RecoverXp(identity, 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("path=launch-guid") && l.Contains("identityMatches=1")
                && l.Contains("nameOnlyIgnored=1"));
        }

        // ----------------------------------------------------------------
        // Chained segments of one launch
        // ----------------------------------------------------------------

        [Fact]
        public void ChainedSegments_CreditTheLastEndedSegment_OrTheBracketingOne()
        {
            // One launch recorded as three segments (an optimizer split plus a continuation);
            // a debris recording of the same craft name carries its OWN launch guid. A
            // recovery after the flight credits the last segment that ended; a recovery
            // inside a segment credits the segment that brackets it. The debris never
            // competes, even though it shares the name.
            AddRec("rec-seg-1", "Jumping Flea", 100.0, 300.0, GuidA);
            AddRec("rec-seg-2", "Jumping Flea", 300.0, 600.0, GuidA);
            AddRec("rec-seg-3", "Jumping Flea", 600.0, 900.0, GuidA);
            AddRec("rec-debris", "Jumping Flea", 300.0, 2000.0, GuidB, TerminalState.Destroyed);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA, 2905720181u), 1000.0);
            Assert.Equal("rec-seg-3", picked.RecordingId);
            Assert.Equal(RecoveryPickTier.MostRecentEnded, picked.Tier);
            Assert.Equal(RecoveryPickPath.LaunchGuid, picked.Path);
            Assert.Equal(3, picked.SurvivorCount);

            var mid = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA, 2905720181u), 450.0);
            Assert.Equal("rec-seg-2", mid.RecordingId);
            Assert.Equal(RecoveryPickTier.Bracketing, mid.Tier);

            // The XP leg writes over three survivors on a weak tier: one known launch.
            Assert.Equal("rec-seg-3", RecoverXp(
                RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA, 2905720181u), 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId: vessel='Jumping Flea'")
                && l.Contains("path=launch-guid")
                && l.Contains("survivors=3")
                && l.Contains("guidDropped=n/a")
                && l.Contains("tier=most-recent-ended")
                && l.Contains("pick=rec-seg-3"));
        }

        [Fact]
        public void LaunchGuidPath_StageTwoRefusalCannotFire_BecauseEverySurvivorIsOneKnownLaunch()
        {
            // By construction: the launch-guid path's set is "recordings carrying THIS guid",
            // so the corroboration clause reads one-known-launch on every weak-tier pick, and
            // the refusal - which needs an uncorroborated set - cannot fire.
            AddRec("rec-seg-1", "Hopper", 100.0, 300.0, GuidA);
            AddRec("rec-seg-2", "Hopper", 300.0, 600.0, GuidA);
            AddRec("rec-legacy", "Hopper", 50.0, 700.0, null);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 2905720181u), 1000.0);
            Assert.Equal(RecoveryPickPath.LaunchGuid, picked.Path);
            Assert.All(picked.Survivors, r => Assert.Equal(GuidA, r.RecordedVesselGuid));
            var verdict = RecoveryPickAmbiguity.Evaluate(picked.Survivors, picked.Tier);
            Assert.True(RecoveryPickAmbiguity.IsWeakTier(picked.Tier));
            Assert.Equal(SurvivorLaunchCorroboration.OneKnownLaunch, verdict.Corroboration);
            Assert.False(verdict.IsAmbiguous);

            Assert.Equal("rec-seg-2", RecoverXp(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 2905720181u), 1000.0));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=ambiguous-recovery-recording"));
        }

        // ----------------------------------------------------------------
        // Legacy id-less recordings: the name fallback, stage 1 and stage 2 intact
        // ----------------------------------------------------------------

        [Fact]
        public void LegacyIdLessRecording_UsesTheNameFallback()
        {
            AddRec("rec-legacy", "Hopper", 100.0, 900.0, null);

            // The recovery carries a guid no recording carries.
            Assert.Equal("rec-legacy", RecoverFunds(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 2905720181u), 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId path: vessel='Hopper'")
                && l.Contains("path=name-fallback")
                && l.Contains("reason=no-recording-carries-launch-guid"));

            // And a recovery with no guid at all.
            logLines.Clear();
            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper"), 1000.0);
            Assert.Equal("rec-legacy", picked.RecordingId);
            Assert.Equal(RecoveryPickPath.NameFallback, picked.Path);
            Assert.Contains(logLines, l =>
                l.Contains("path=name-fallback") && l.Contains("reason=live-launch-guid-unknown"));
        }

        [Fact]
        public void NameFallback_KeepsTheStageOneFilter_AndTheStageTwoRefusal()
        {
            // Two id-less same-name recordings plus one of a conclusively different launch,
            // recovered by a vessel whose guid no recording carries. Stage 1 drops the other
            // launch; the two id-less survivors on a weak tier are uncorroborated, so the XP
            // leg refuses while funds keep their (revisable) pick.
            AddRec("rec-legacy-1", "Hopper", 100.0, 300.0, null);
            AddRec("rec-legacy-2", "Hopper", 400.0, 600.0, null);
            AddRec("rec-other-launch", "Hopper", 650.0, 800.0, GuidB);
            var identity = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 2905720181u);

            Assert.Null(RecoverXp(identity, 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("Recovery kerbal XP refused")
                && l.Contains("reason=ambiguous-recovery-recording")
                && l.Contains("survivors=2")
                && l.Contains("guidDropped=1")
                && l.Contains("corroboration=unknown-launch-guid"));
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter")
                && l.Contains("dropped=1")
                && l.Contains("reason=guid-conclusive-mismatch"));

            Assert.Equal("rec-legacy-2", RecoverFunds(identity, 1000.0));
        }

        // ----------------------------------------------------------------
        // A recovered SPAWNED vessel
        // ----------------------------------------------------------------

        [Fact]
        public void RecoveredSpawnedVessel_CreditsTheRecordingThatSpawnedIt()
        {
            // A genuine Parsek spawn regenerates the vessel guid and pid
            // (VesselSpawner.RegenerateVesselIdentity), so the spawned vessel's guid matches
            // no recording - the source recording still carries the ORIGINAL launch's guid.
            // Its KSP-unique spawn pid is stamped on the source as SpawnedVesselPersistentId,
            // and that names the recording. Before stage 3 the stage-1 filter dropped the
            // source as a conclusively different launch and every leg credited nothing.
            var earlier = AddRec("rec-seg-1", "Hopper", 100.0, 400.0, GuidA);
            var source = AddRec("rec-seg-2", "Hopper", 400.0, 900.0, GuidA);
            source.SpawnedVesselPersistentId = 777001u;
            var identity = RecoveredVesselIdentity.FromRawName("Hopper", GuidSpawn, 777001u);

            Assert.Equal("rec-seg-2", RecoverFunds(identity, 1000.0));
            Assert.Equal("rec-seg-2", RecoverScience(identity, 1000.0));
            Assert.Equal("rec-seg-2", RecoverXp(identity, 1000.0));
            Assert.Contains(logLines, l =>
                l.Contains("path=spawn-pid")
                && l.Contains("identityMatches=1")
                && l.Contains("spawnPid=777001"));

            // #1946's safety net consumes the corrected pick: the source now reads recovered,
            // so its end-of-recording spawn is not repeated; the earlier segment does not.
            Assert.True(RecoveredRecordingEvidence.IsRecovered(source, Ledger.Actions, out _));
            Assert.False(RecoveredRecordingEvidence.IsRecovered(earlier, Ledger.Actions, out _));

            // Negative control: the same spawned vessel recovered through a seam with no pid
            // falls back to name + filter, which drops both segments (conclusive guid
            // mismatch) - the pre-stage-3 outcome.
            Assert.Null(LedgerOrchestrator.PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidSpawn), 1000.0));
        }

        [Fact]
        public void SpawnPidPath_RefusesAnAdoptionStamp_WhosePidIsTheCraftBakedOne()
        {
            // An adoption stamp (SpawnedVesselPersistentId == VesselPersistentId) carries the
            // craft-baked pid every launch of the craft shares, so a pid match there proves
            // nothing; a recovery of a DIFFERENT launch with that pid must not reach it.
            var rec = AddRec("rec-adopted", "Hopper", 100.0, 900.0, GuidA);
            rec.SpawnedVesselPersistentId = rec.VesselPersistentId;

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB, rec.VesselPersistentId), 1000.0);
            Assert.Null(picked.RecordingId);
            Assert.Equal(RecoveryPickPath.NameFallback, picked.Path);
            Assert.Equal(1, picked.GuidDropped);
        }

        [Fact]
        public void SpawnedVesselFlownAndRecordedAgain_CreditsItsOwnNewRecording()
        {
            // Once the player flies the spawned vessel, its new recording carries the spawned
            // vessel's own fresh guid, and the launch-guid path wins over the spawn source.
            var source = AddRec("rec-source", "Hopper", 100.0, 900.0, GuidA);
            source.SpawnedVesselPersistentId = 777002u;
            AddRec("rec-flown-after-spawn", "Hopper", 1000.0, 1500.0, GuidSpawn);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidSpawn, 777002u), 1600.0);
            Assert.Equal("rec-flown-after-spawn", picked.RecordingId);
            Assert.Equal(RecoveryPickPath.LaunchGuid, picked.Path);
        }

        // ----------------------------------------------------------------
        // Re-Fly provisional admission on the launch-guid path
        // ----------------------------------------------------------------

        [Fact]
        public void LaunchGuidPath_AdmitsAnIdLessSessionProvisional_SoTheBracketTieStillHolds()
        {
            // TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT survives stage 3. The origin carries
            // the launch guid; the live session's provisional has not inherited it yet (the
            // placeholder shape). It is admitted on name - the legacy fallback for an id-less
            // member - and wins the tier-1 bracket tie, so the mid-session payout is not
            // tagged to the recording the merge will supersede.
            InstallReFlySession("rec-provisional", "rec-origin");
            AddRec("rec-origin", "Reusable", 100.0, 900.0, GuidA, TerminalState.Destroyed);
            var provisional = AddRec("rec-provisional", "Reusable", 500.0, 700.0, null,
                TerminalState.Orbiting);
            provisional.MergeState = MergeState.NotCommitted;

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Reusable", GuidA, 2905720181u), 600.0);
            Assert.Equal("rec-provisional", picked.RecordingId);
            Assert.Equal(RecoveryPickPath.LaunchGuid, picked.Path);
            Assert.Contains(logLines, l =>
                l.Contains("path=launch-guid") && l.Contains("unknownGuidSessionProvisionalAdmitted=True"));
            Assert.Contains(logLines, l =>
                l.Contains("bracketTie=session-provisional") && l.Contains("pick=rec-provisional"));
        }

        private static void InstallReFlySession(string provisionalId, string originId)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_1",
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = provisionalId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    RewindPointId = "rp_1",
                    InvokedUT = 0.0,
                    PreSessionBranchPointIds = new List<string>(),
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
        }

        // ----------------------------------------------------------------
        // Pure predicates
        // ----------------------------------------------------------------

        [Fact]
        public void Predicates_LaunchGuidIsPositiveAndSpawnPidIsGenuineOnly()
        {
            var rec = new Recording
            {
                RecordingId = "r",
                RecordedVesselGuid = GuidA,
                VesselPersistentId = 100u,
                SpawnedVesselPersistentId = 200u
            };
            Assert.True(LedgerOrchestrator.IsPositiveLaunchGuidMatch(rec, GuidA));
            Assert.True(LedgerOrchestrator.IsPositiveLaunchGuidMatch(
                rec, "AAAAAAAA-AAAA-4AAA-AAAA-AAAAAAAAAAAA"));
            Assert.False(LedgerOrchestrator.IsPositiveLaunchGuidMatch(rec, GuidB));
            Assert.False(LedgerOrchestrator.IsPositiveLaunchGuidMatch(rec, null));
            Assert.False(LedgerOrchestrator.IsPositiveLaunchGuidMatch(
                new Recording { RecordedVesselGuid = null }, GuidA));
            Assert.False(LedgerOrchestrator.IsPositiveLaunchGuidMatch(null, GuidA));

            Assert.True(LedgerOrchestrator.IsGenuineSpawnPidMatch(rec, 200u));
            Assert.False(LedgerOrchestrator.IsGenuineSpawnPidMatch(rec, 100u));
            Assert.False(LedgerOrchestrator.IsGenuineSpawnPidMatch(rec, 0u));
            rec.SpawnedVesselPersistentId = 100u; // adoption stamp
            Assert.False(LedgerOrchestrator.IsGenuineSpawnPidMatch(rec, 100u));
            rec.SpawnedVesselPersistentId = 0u;
            Assert.False(LedgerOrchestrator.IsGenuineSpawnPidMatch(rec, 0u));
        }

        [Fact]
        public void Identity_PersistentIdIsCarriedButNotPartOfNameMatchingOrTheLogSurface()
        {
            var withPid = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 4242u);
            var withoutPid = RecoveredVesselIdentity.FromRawName("Hopper", GuidA);
            Assert.Equal(4242u, withPid.PersistentId);
            Assert.Equal(0u, withoutPid.PersistentId);
            Assert.Equal(GuidA, withPid.LaunchGuid);
            Assert.True(withPid.Matches(withoutPid));
            Assert.Equal(withoutPid.FormatForLog(), withPid.FormatForLog());
        }
    }
}
