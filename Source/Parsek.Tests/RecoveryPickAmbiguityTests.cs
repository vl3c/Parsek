using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY, STAGE 2: the KERBAL XP leg refuses to
    /// write against a recovery pick nothing determines, with
    /// <c>reason=ambiguous-recovery-recording</c>.
    ///
    /// <para>
    /// Stage 1 (shipped 2026-08-28, flown 2026-09-02) drops from the candidate set any
    /// name-matching recording whose recorded launch guid CONCLUSIVELY differs from the
    /// recovering vessel's. Stage 2 is the tie-breaker of last resort behind it: when the
    /// filtered set STILL holds candidates that cannot be corroborated as one launch, and the
    /// winner is chosen by a weak EndUT-ordering tier, the XP write is refused rather than
    /// guessed. Funds and science keep the stage-1 pick - their rows are re-derived
    /// idempotently on every recalc, so a mis-scoped one is revisable; the XP row appends
    /// career-log entries through a facade with no remove counterpart.
    /// </para>
    ///
    /// <para>
    /// <b>THE NEGATIVE PROOF IS THE POINT OF THIS FILE.</b> The entry's "What NOT to do"
    /// paragraph names the way this fix can go wrong: a bare tier-strength refusal would
    /// refuse the very recoveries stage 1 was written to capture, and <c>L4</c>'s
    /// <c>KerbalXp</c> facet would go vacuous. That is not a hypothetical - the ordinary
    /// single-launch career recovery lands on <c>tier=most-recent-ended</c> with TWO
    /// survivors, because one launch is recorded as a CHAIN of segments.
    /// <c>CommittedCareerFixture_RecoveryPickIsNotAmbiguous</c> walks the committed career
    /// fixture through the real picker and pins that it still writes.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryPickAmbiguityTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbb4bbbbbbbbbbbbbbbbbbb";

        private readonly List<string> logLines = new List<string>();

        public RecoveryPickAmbiguityTests()
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

        private static Recording Rec(
            string id, string vesselName, double startUt, double endUt, string launchGuid)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                PreLaunchFunds = 50000.0,
                RecordedVesselGuid = launchGuid,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = endUt, funds = 40000.0 });
            return rec;
        }

        private static Recording AddRec(
            string id, string vesselName, double startUt, double endUt, string launchGuid)
        {
            var rec = Rec(id, vesselName, startUt, endUt, launchGuid);
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

        // ================================================================
        // The pure predicate
        // ================================================================

        [Fact]
        public void Evaluate_TwoUnknownGuidSurvivorsOnAWeakTier_IsAmbiguous()
        {
            // THE SHAPE STAGE 2 EXISTS FOR: two launches of one craft name whose recordings
            // carry NO launch guid, so stage 1's filter cannot separate them (unknown on
            // either side is never conclusive) and both reach the tier walk. The winner is
            // whichever ended later - an ordering, not an identification.
            var survivors = new List<Recording>
            {
                Rec("rec-launch-1", "Hopper", 100.0, 500.0, null),
                Rec("rec-launch-2", "Hopper", 600.0, 900.0, null),
            };

            var result = RecoveryPickAmbiguity.Evaluate(
                survivors, RecoveryPickTier.MostRecentEnded);

            Assert.True(result.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.AmbiguousReason, result.Reason);
            Assert.Equal(2, result.SurvivorCount);
            Assert.Equal(SurvivorLaunchCorroboration.UnknownLaunchGuid, result.Corroboration);
        }

        [Fact]
        public void Evaluate_TheStageOneWin_LiveGuidCollapsesTheSetToOneSurvivor()
        {
            // The mirror of the cell above, and the reason stage 1 had to land first: when
            // the recovering vessel's guid IS known and the recordings carry theirs, the
            // filter drops the foreign launch BEFORE this predicate ever runs. One survivor
            // is never ambiguous, so the row is written - the stage-1 win, preserved.
            var nameMatches = new List<Recording>
            {
                Rec("rec-launch-1", "Hopper", 100.0, 500.0, GuidA),
                Rec("rec-launch-2", "Hopper", 600.0, 900.0, GuidB),
            };

            var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                nameMatches, GuidB, out int dropped);
            Assert.Equal(1, dropped);

            var result = RecoveryPickAmbiguity.Evaluate(
                survivors, RecoveryPickTier.MostRecentEnded);

            Assert.False(result.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.SingleSurvivorReason, result.Reason);
            Assert.Equal(1, result.SurvivorCount);
        }

        [Fact]
        public void Evaluate_SeveralSurvivorsOfONELaunch_IsNotAmbiguous()
        {
            // CLAUSE 3, AND THE WHOLE REASON IT EXISTS. One launch is recorded as a CHAIN of
            // segments, so the ordinary career recovery reaches a weak tier with more than
            // one survivor. They all carry the same KNOWN launch guid, so the set is
            // positively one launch and there is nothing to be ambiguous about.
            var survivors = new List<Recording>
            {
                Rec("rec-seg-0", "Jumping Flea", 10.0, 342.06, GuidA),
                Rec("rec-seg-1", "Jumping Flea", 342.06, 347.02, GuidA),
            };

            var result = RecoveryPickAmbiguity.Evaluate(
                survivors, RecoveryPickTier.MostRecentEnded);

            Assert.False(result.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.OneCorroboratedLaunchReason, result.Reason);
            Assert.Equal(SurvivorLaunchCorroboration.OneKnownLaunch, result.Corroboration);
        }

        [Fact]
        public void Evaluate_TwoDistinctKnownLaunchesOnAWeakTier_IsAmbiguous()
        {
            // Reachable when the recovery seam supplies NO live guid (the filter is inert)
            // while the recordings carry theirs. The set provably spans two launches, so
            // ranking them by EndUT is a guess.
            var survivors = new List<Recording>
            {
                Rec("rec-launch-1", "Hopper", 100.0, 500.0, GuidA),
                Rec("rec-launch-2", "Hopper", 600.0, 900.0, GuidB),
            };

            var result = RecoveryPickAmbiguity.Evaluate(
                survivors, RecoveryPickTier.MostRecentEnded);

            Assert.True(result.IsAmbiguous);
            Assert.Equal(SurvivorLaunchCorroboration.DistinctKnownLaunches, result.Corroboration);
        }

        [Fact]
        public void Evaluate_GlobalLatestIsWeakToo()
        {
            // The recommendation names both weak tiers ("most-recent-ended or global-latest
            // rather than bracketing"), and tier 3 is if anything the weaker: it fires only
            // when NOTHING brackets the recovery and NOTHING ended before it, so every
            // survivor ends after the recovery and the ordering has no relation to it.
            Assert.True(RecoveryPickAmbiguity.IsWeakTier(RecoveryPickTier.MostRecentEnded));
            Assert.True(RecoveryPickAmbiguity.IsWeakTier(RecoveryPickTier.GlobalLatest));
            Assert.False(RecoveryPickAmbiguity.IsWeakTier(RecoveryPickTier.Bracketing));
            Assert.False(RecoveryPickAmbiguity.IsWeakTier(RecoveryPickTier.None));

            var survivors = new List<Recording>
            {
                Rec("rec-launch-1", "Hopper", 2000.0, 3000.0, null),
                Rec("rec-launch-2", "Hopper", 2500.0, 4000.0, null),
            };

            var result = RecoveryPickAmbiguity.Evaluate(survivors, RecoveryPickTier.GlobalLatest);
            Assert.True(result.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.AmbiguousReason, result.Reason);
        }

        [Fact]
        public void Evaluate_BracketingIsNeverAmbiguous_EvenWithDistinctKnownLaunches()
        {
            // A DELIBERATE REFUSAL TO WIDEN THE ENTRY'S RULE. The recommendation names
            // exactly two weak tiers, "rather than bracketing", and bracketing is a positive
            // temporal fact about the winning candidate (it CONTAINS the recovery UT) rather
            // than an ordering among candidates. Tier 1 also already carries a reasoned
            // tie-break of its own (the live session's provisional outranks largest-EndUT).
            var survivors = new List<Recording>
            {
                Rec("rec-launch-1", "Hopper", 100.0, 5000.0, GuidA),
                Rec("rec-launch-2", "Hopper", 200.0, 6000.0, GuidB),
            };

            var result = RecoveryPickAmbiguity.Evaluate(survivors, RecoveryPickTier.Bracketing);

            Assert.False(result.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.BracketingReason, result.Reason);
            // The corroboration is still MEASURED and reported - the branch is a policy
            // decision about the tier, not a claim that the set is unambiguous.
            Assert.Equal(SurvivorLaunchCorroboration.DistinctKnownLaunches, result.Corroboration);
        }

        [Fact]
        public void Evaluate_SingleSurvivorIsNeverAmbiguous_OnEveryTier()
        {
            foreach (var tier in new[]
                     {
                         RecoveryPickTier.Bracketing,
                         RecoveryPickTier.MostRecentEnded,
                         RecoveryPickTier.GlobalLatest,
                     })
            {
                // Known guid and unknown guid alike: one candidate, nothing to choose between.
                foreach (string guid in new[] { GuidA, null })
                {
                    var result = RecoveryPickAmbiguity.Evaluate(
                        new List<Recording> { Rec("rec-only", "Hopper", 100.0, 900.0, guid) },
                        tier);
                    Assert.False(result.IsAmbiguous);
                }
            }
        }

        [Fact]
        public void Evaluate_DegenerateInputsAreNamedRatherThanAmbiguous()
        {
            // Empty / no-pick route into the caller's PRE-EXISTING no-recovery-recording
            // refusal, which fires before this predicate is consulted. Named anyway so a log
            // can never show a bare "not ambiguous" that could mean either.
            var empty = RecoveryPickAmbiguity.Evaluate(
                new List<Recording>(), RecoveryPickTier.MostRecentEnded);
            Assert.False(empty.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.NoSurvivorsReason, empty.Reason);

            var nulled = RecoveryPickAmbiguity.Evaluate(null, RecoveryPickTier.MostRecentEnded);
            Assert.False(nulled.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.NoSurvivorsReason, nulled.Reason);

            var noTier = RecoveryPickAmbiguity.Evaluate(
                new List<Recording>
                {
                    Rec("a", "Hopper", 1.0, 2.0, null),
                    Rec("b", "Hopper", 3.0, 4.0, null),
                },
                RecoveryPickTier.None);
            Assert.False(noTier.IsAmbiguous);
            Assert.Equal(RecoveryPickAmbiguity.NoPickReason, noTier.Reason);
        }

        [Fact]
        public void Classify_UsesPositiveGuidCorroboration_NotRecordingsShareLaunch()
        {
            // WHY VesselLaunchIdentity.RecordingsShareLaunch IS THE WRONG HELPER HERE.
            // It requires equal persistentId plus guids that do not CONCLUSIVELY differ, and
            // persistentId is CRAFT-BAKED: two launches of one craft carry the same one. So
            // it reads TRUE for two guid-less launches of one craft - exactly the ambiguous
            // shape stage 2 must catch. Measured, not asserted from the doc comment.
            var launch1 = Rec("rec-launch-1", "Hopper", 100.0, 500.0, null);
            var launch2 = Rec("rec-launch-2", "Hopper", 600.0, 900.0, null);
            launch1.VesselPersistentId = 2905720181;
            launch2.VesselPersistentId = 2905720181;

            Assert.True(VesselLaunchIdentity.RecordingsShareLaunch(launch1, launch2));
            Assert.Equal(
                SurvivorLaunchCorroboration.UnknownLaunchGuid,
                RecoveryPickAmbiguity.ClassifySurvivorLaunches(
                    new List<Recording> { launch1, launch2 }));
        }

        [Fact]
        public void Classify_DistinctKnownLaunchesOutranksAnUnknownInTheSameSet()
        {
            // Both classes mean "not one launch"; the more specific one is reported so the
            // refusal line says WHY rather than only that.
            var set = new List<Recording>
            {
                Rec("a", "Hopper", 1.0, 2.0, GuidA),
                Rec("b", "Hopper", 3.0, 4.0, null),
                Rec("c", "Hopper", 5.0, 6.0, GuidB),
            };
            Assert.Equal(
                SurvivorLaunchCorroboration.DistinctKnownLaunches,
                RecoveryPickAmbiguity.ClassifySurvivorLaunches(set));

            // A null entry is fail-closed: treated as an unknown guid, never as "same".
            Assert.Equal(
                SurvivorLaunchCorroboration.UnknownLaunchGuid,
                RecoveryPickAmbiguity.ClassifySurvivorLaunches(
                    new List<Recording> { Rec("a", "Hopper", 1.0, 2.0, GuidA), null }));

            // Guid spelling must not fabricate a second launch: NormalizeGuid canonicalizes.
            Assert.Equal(
                SurvivorLaunchCorroboration.OneKnownLaunch,
                RecoveryPickAmbiguity.ClassifySurvivorLaunches(
                    new List<Recording>
                    {
                        Rec("a", "Hopper", 1.0, 2.0, GuidA),
                        Rec("b", "Hopper", 3.0, 4.0, new Guid(GuidA).ToString("D").ToUpperInvariant()),
                    }));
        }

        [Fact]
        public void FormatSurvivorIds_IsBounded()
        {
            var many = new List<Recording>();
            for (int i = 0; i < RecoveryPickAmbiguity.MaxLoggedSurvivorIds + 3; i++)
                many.Add(Rec("rec-" + i.ToString(), "Hopper", i, i + 1, null));

            string text = RecoveryPickAmbiguity.FormatSurvivorIds(many);
            Assert.Contains("rec-0", text);
            Assert.Contains("+3 more", text);
            Assert.DoesNotContain("rec-" + RecoveryPickAmbiguity.MaxLoggedSurvivorIds.ToString(), text);

            Assert.Equal("(none)", RecoveryPickAmbiguity.FormatSurvivorIds(null));
            Assert.Equal("(none)", RecoveryPickAmbiguity.FormatSurvivorIds(new List<Recording>()));
        }

        [Fact]
        public void Evaluate_ReadsThePostFilterSet_SoItCannotReinstateADroppedCandidate()
        {
            // Stage 1's MONOTONICITY RULE, carried into stage 2. If the ambiguity check read
            // the pre-filter name-match set it would see a candidate the filter removed and
            // could refuse a recovery the filter had already resolved correctly. The picker
            // hands out its post-filter list; this pins the difference the two sets make.
            var nameMatches = new List<Recording>
            {
                Rec("rec-foreign", "Hopper", 100.0, 500.0, GuidA),
                Rec("rec-mine", "Hopper", 600.0, 900.0, GuidB),
            };
            var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                nameMatches, GuidB, out _);

            Assert.True(RecoveryPickAmbiguity
                .Evaluate(nameMatches, RecoveryPickTier.MostRecentEnded).IsAmbiguous);
            Assert.False(RecoveryPickAmbiguity
                .Evaluate(survivors, RecoveryPickTier.MostRecentEnded).IsAmbiguous);
        }

        // ================================================================
        // The picker exposes the tier and the post-filter survivors
        // ================================================================

        [Fact]
        public void Picker_ReportsTheTierAndThePostFilterSurvivors()
        {
            AddRec("rec-launch-A", "Hopper", 100.0, 500.0, GuidA);
            AddRec("rec-launch-B", "Hopper", 600.0, 900.0, GuidB);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Equal("rec-launch-B", picked.RecordingId);
            Assert.Equal(RecoveryPickTier.MostRecentEnded, picked.Tier);
            Assert.Equal(2, picked.NameMatchCount);
            Assert.Equal(1, picked.GuidDropped);
            Assert.Equal("rec-launch-B", Assert.Single(picked.Survivors).RecordingId);

            // The id-only overload is the same decision, so funds and science see no change.
            Assert.Equal(
                picked.RecordingId,
                LedgerOrchestrator.PickRecoveryRecordingId(
                    RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0));
        }

        [Fact]
        public void Picker_BracketingTierIsReportedAsSuch()
        {
            AddRec("rec-spanning", "Hopper", 100.0, 5000.0, GuidA);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA), 1000.0);

            Assert.Equal(RecoveryPickTier.Bracketing, picked.Tier);
        }

        [Fact]
        public void Picker_NoCandidate_ReportsTierNoneAndAnEmptySurvivorSet()
        {
            AddRec("rec-other-launch", "Hopper", 100.0, 5000.0, GuidA);

            var picked = LedgerOrchestrator.PickRecoveryRecording(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Null(picked.RecordingId);
            Assert.Equal(RecoveryPickTier.None, picked.Tier);
            Assert.Empty(picked.Survivors);
            Assert.Equal(1, picked.GuidDropped);
        }

        // ================================================================
        // The XP leg, end to end
        // ================================================================

        [Fact]
        public void XpLeg_TwoLaunchesSameNameWithNoRecordedGuids_RefusesTheWrite()
        {
            // The orchestrator-level proof. Two launches of one craft name, neither
            // recording carrying a launch guid, recovered with no live guid either - so
            // stage 1's filter is inert and both reach a weak tier. The XP row is the
            // irreversible one, so it is not written.
            AddRec("rec-launch-1", "Hopper", 100.0, 500.0, null);
            AddRec("rec-launch-2", "Hopper", 600.0, 900.0, null);

            var events = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) };
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                events, RecoveredVesselIdentity.FromRawName("Hopper"), 1000.0);

            Assert.Equal(0, rows);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalExperience);

            string refusal = Assert.Single(logLines.Where(l =>
                l.Contains("Recovery kerbal XP refused") &&
                l.Contains("reason=ambiguous-recovery-recording")));
            Assert.Contains("survivors=2", refusal);
            Assert.Contains("tier=most-recent-ended", refusal);
            Assert.Contains("corroboration=unknown-launch-guid", refusal);
            Assert.Contains("survivorIds=rec-launch-1,rec-launch-2", refusal);
            Assert.Contains("wouldHavePicked=rec-launch-2", refusal);
        }

        [Fact]
        public void XpLeg_TheMirror_LiveGuidResolvesTheAmbiguityAndTheRowIsWritten()
        {
            // Same fixture shape, one difference: the recordings carry their launch guids
            // and the recovering vessel supplies its own, so stage 1 drops the foreign
            // launch and the survivor set holds one recording. The row IS written. This is
            // the pair that shows stage 2 refusing ONLY what stage 1 could not resolve.
            AddRec("rec-launch-1", "Hopper", 100.0, 500.0, GuidA);
            AddRec("rec-launch-2", "Hopper", 600.0, 900.0, GuidB);

            var events = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) };
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                events, RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Equal(1, rows);
            var xp = Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience);
            Assert.Equal("rec-launch-2", xp.RecordingId);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=ambiguous-recovery-recording"));
        }

        [Fact]
        public void XpLeg_ChainedSegmentsOfOneLaunch_StillWriteTheRow()
        {
            // The measured ordinary case, in miniature: one launch, two chained recordings,
            // both ended before the recovery, so tier=most-recent-ended over TWO survivors.
            // Clauses 1 and 2 both hold; clause 3 does not, and the row is written.
            AddRec("rec-seg-0", "Jumping Flea", 10.0, 342.06, GuidA);
            AddRec("rec-seg-1", "Jumping Flea", 342.06, 347.02, GuidA);

            var events = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 347.5) };
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                events, RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA), 347.5);

            Assert.Equal(1, rows);
            Assert.Equal(
                "rec-seg-1",
                Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience).RecordingId);
        }

        [Fact]
        public void XpLeg_ARefusalIsNotSticky_ALaterResolvedRecoveryWrites()
        {
            // Idempotence and reversibility of the refusal, in the only sense that applies:
            // the XP leg is reached once per RECOVERY (from
            // GameStateRecorder.OnVesselRecoveryProcessingForExperience), never from a
            // recalc, so a refusal cannot repeat for one recovery. And nothing about it is
            // sticky - once the ambiguity resolves (here, a live guid), the very next
            // recovery writes its row.
            var launch1 = AddRec("rec-launch-1", "Hopper", 100.0, 500.0, null);
            var launch2 = AddRec("rec-launch-2", "Hopper", 600.0, 900.0, null);

            Assert.Equal(0, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) },
                RecoveredVesselIdentity.FromRawName("Hopper"), 1000.0));

            // The ambiguity resolves: BOTH recordings now carry their launch guids, so
            // stage 1's filter can drop the foreign launch. One known guid is not enough -
            // an unknown one on the other side keeps the set uncorroborated, which is the
            // fail-closed half of the rule.
            launch1.RecordedVesselGuid = GuidA;
            launch2.RecordedVesselGuid = GuidB;

            Assert.Equal(1, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Bill Kerman", 1200.0) },
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1200.0));
            Assert.Equal(
                "rec-launch-2",
                Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience).RecordingId);
        }

        [Fact]
        public void XpLeg_FundsAndScienceLegsKeepTheStageOnePick_OnlyXpRefuses()
        {
            // THE SCOPE OF THE REFUSAL, asserted rather than described. On the SAME ambiguous
            // fixture the shared picker still resolves an owner - so the funds and science
            // legs, which call PickRecoveryRecordingId and whose rows are re-derived
            // idempotently on every recalc, are unaffected. Only the irreversible leg
            // refuses.
            AddRec("rec-launch-1", "Hopper", 100.0, 500.0, null);
            AddRec("rec-launch-2", "Hopper", 600.0, 900.0, null);

            var identity = RecoveredVesselIdentity.FromRawName("Hopper");

            Assert.Equal("rec-launch-2", LedgerOrchestrator.PickRecoveryRecordingId(identity, 1000.0));
            Assert.Equal(0, LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) }, identity, 1000.0));
        }

        // ================================================================
        // THE NEGATIVE PROOF: the committed career fixture still writes
        // ================================================================

        [Fact]
        public void CommittedCareerFixture_RecoveryPickIsNotAmbiguous()
        {
            // Walks Source/Parsek.Tests/Fixtures/C2CareerPostFix - the career EARNED by a
            // driven flight (harness run 2026-08-20_1925_L3-career-science-recover_run2) and
            // the subject L4's KerbalXp facet consumes - through the REAL picker, and pins
            // that stage 2 leaves it alone.
            //
            // WHY THIS CELL IS THE POINT OF THE FILE. The fixture's recovery lands on
            // survivors=2 tier=most-recent-ended: one launch recorded as TWO chained
            // segments, both ended before the recovery. Clauses 1 and 2 of the ambiguity
            // predicate BOTH hold here. A stage 2 written to the recommendation's literal
            // wording would refuse this recovery - i.e. refuse the very shape stage 1 was
            // built to capture - and L4's facet, which needs a KerbalExperience row to
            // compare anything at all, would go vacuous. The numbers below are MEASURED off
            // the committed bytes, not asserted from the entry.
            var recordings = LoadCommittedCareerFixtureRecordings();
            Assert.Equal(2, recordings.Count);
            foreach (var rec in recordings)
                RecordingStore.AddRecordingWithTreeForTesting(rec);

            // The fixture's own recovery moment and vessel, read off its ledger's
            // KerbalExperience row rather than hard-coded.
            var xpRow = LoadCommittedCareerFixtureXpRow();
            string vesselName = recordings[0].VesselName;
            Assert.Equal("Jumping Flea", vesselName);

            // The guid the seam would supply live is the recorded launch's own: this is one
            // launch, recovered. Both directions are proven - with the guid and without it
            // (a seam that could not supply one), because the fixture's survivors carry the
            // same known guid either way.
            string launchGuid = recordings[0].RecordedVesselGuid;
            Assert.False(string.IsNullOrEmpty(launchGuid));

            foreach (var identity in new[]
                     {
                         RecoveredVesselIdentity.FromRawName(vesselName, launchGuid),
                         RecoveredVesselIdentity.FromRawName(vesselName),
                     })
            {
                var picked = LedgerOrchestrator.PickRecoveryRecording(identity, xpRow.UT);

                // The shape that makes this cell load-bearing: TWO survivors on a WEAK tier.
                Assert.Equal(2, picked.SurvivorCount);
                Assert.Equal(0, picked.GuidDropped);
                Assert.Equal(RecoveryPickTier.MostRecentEnded, picked.Tier);
                Assert.True(RecoveryPickAmbiguity.IsWeakTier(picked.Tier));

                // And the verdict: NOT ambiguous, because the survivors are one launch.
                var ambiguity = RecoveryPickAmbiguity.Evaluate(picked.Survivors, picked.Tier);
                Assert.False(ambiguity.IsAmbiguous);
                Assert.Equal(RecoveryPickAmbiguity.OneCorroboratedLaunchReason, ambiguity.Reason);
                Assert.Equal(SurvivorLaunchCorroboration.OneKnownLaunch, ambiguity.Corroboration);

                // The pick is unchanged from what the fixture's ledger actually recorded.
                Assert.Equal(xpRow.RecordingId, picked.RecordingId);
            }
        }

        [Fact]
        public void CommittedCareerFixture_XpRowIsStillWrittenThroughTheRealXpLeg()
        {
            // The same fixture driven through the production XP entry point, so the negative
            // proof covers the refusal SITE and not only the predicate.
            var recordings = LoadCommittedCareerFixtureRecordings();
            foreach (var rec in recordings)
                RecordingStore.AddRecordingWithTreeForTesting(rec);

            var xpRow = LoadCommittedCareerFixtureXpRow();
            Ledger.Clear();

            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", xpRow.UT) },
                RecoveredVesselIdentity.FromRawName(
                    recordings[0].VesselName, recordings[0].RecordedVesselGuid),
                xpRow.UT);

            Assert.Equal(1, rows);
            Assert.Equal(
                xpRow.RecordingId,
                Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience).RecordingId);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=ambiguous-recovery-recording"));
        }

        // ----------------------------------------------------------------
        // Committed-career-fixture loaders
        // ----------------------------------------------------------------

        private static string ResolveCareerFixtureDir()
        {
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            string dir = Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix");
            Assert.True(Directory.Exists(dir), $"C2CareerPostFix fixture dir not found at '{dir}'");
            return dir;
        }

        /// <summary>
        /// The fixture's RECORDING nodes, deserialized through the PRODUCTION codec so the
        /// cell reads the committed bytes rather than a hand-written copy of them.
        /// </summary>
        private static List<Recording> LoadCommittedCareerFixtureRecordings()
        {
            ConfigNode root = ConfigNode.Load(Path.Combine(ResolveCareerFixtureDir(), "persistent.sfs"));
            Assert.NotNull(root);

            ConfigNode treeNode = null;
            foreach (ConfigNode game in root.GetNodes("GAME"))
            {
                foreach (ConfigNode scenario in game.GetNodes("SCENARIO"))
                {
                    if (scenario.GetValue("name") != "ParsekScenario") continue;
                    var trees = scenario.GetNodes("RECORDING_TREE");
                    if (trees.Length > 0) treeNode = trees[0];
                }
            }
            Assert.NotNull(treeNode);

            var result = new List<Recording>();
            foreach (ConfigNode recNode in treeNode.GetNodes("RECORDING"))
            {
                var rec = new Recording();
                RecordingTreeRecordCodec.LoadRecordingFrom(recNode, rec);
                RecordingTreeRecordCodec.LoadRecordingResourceAndState(recNode, rec);
                result.Add(rec);
            }
            return result;
        }

        private static GameAction LoadCommittedCareerFixtureXpRow()
        {
            string path = Path.Combine(ResolveCareerFixtureDir(), "Parsek", "GameState", "ledger.pgld");
            Assert.True(File.Exists(path), $"fixture ledger not found at '{path}'");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C2CareerPostFix fixture");
            return Assert.Single(
                Ledger.Actions.Where(a => a.Type == GameActionType.KerbalExperience));
        }
    }
}
