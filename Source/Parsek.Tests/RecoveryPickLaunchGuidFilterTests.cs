using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY, STAGE 1: guid corroboration as a FILTER
    /// on the recovery correlator.
    ///
    /// <para>
    /// <c>LedgerOrchestrator.PickRecoveryRecordingId</c> matched candidate recordings by
    /// vessel NAME and then ranked them by a UT tier, never consulting the launch-unique
    /// <c>Vessel.id</c>. Two launches of the same craft name therefore differed only by UT
    /// ordering. All three recovery legs (funds, science, XP) share that picker, and the XP
    /// leg is where a wrong pick becomes IRREVERSIBLE: a <c>KerbalExperience</c> row feeds
    /// <c>KerbalsModule.ReassertCareerLogEntries</c>, whose facade appends career entries with
    /// no remove counterpart, so a mis-scoped row is walked back only by a tombstone written
    /// by the WRONG merge.
    /// </para>
    ///
    /// <para>
    /// Stage 1 drops from the candidate set any name-matching recording whose
    /// <c>RecordedVesselGuid</c> CONCLUSIVELY differs from the recovering vessel's live guid
    /// (<c>VesselLaunchIdentity.GuidsConclusivelyDiffer</c>: an unknown guid on EITHER side is
    /// not conclusive). Two properties make it the safe half and both are pinned here:
    /// it is MONOTONE (survivors are a subsequence of the input, so a pick that was already
    /// correct cannot become wrong), and it DEGRADES to the historical name+UT behavior
    /// exactly when a guid is missing.
    /// </para>
    ///
    /// <para>
    /// Stage 2 - refusing the XP write on an ambiguous filtered set decided by a WEAK tier -
    /// is deliberately NOT implemented and NOT tested here. See the todo entry.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryPickLaunchGuidFilterTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbb4bbbbbbbbbbbbbbbbbbb";

        private readonly List<string> logLines = new List<string>();

        public RecoveryPickLaunchGuidFilterTests()
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

        // ----------------------------------------------------------------
        // The predicate: conclusive means BOTH sides known AND different
        // ----------------------------------------------------------------

        [Fact]
        public void Predicate_IsConclusiveOnlyWhenBothGuidsAreKnownAndDiffer()
        {
            var recA = Rec("rec-a", "Hopper", 100.0, 900.0, GuidA);

            // Both known, different launches: the only conclusive case.
            Assert.True(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(recA, GuidB));

            // Both known, same launch.
            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(recA, GuidA));

            // Live guid unknown (the seam could not supply one) - NOT conclusive. This is
            // the whole reason IsPositivelySameLaunch (which needs a positive guid on both
            // sides) is the wrong shape here: it would drop this candidate.
            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(recA, null));
            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(recA, ""));

            // Recorded guid unknown (a legacy / pre-guid recording) - NOT conclusive.
            var legacy = Rec("rec-legacy", "Hopper", 100.0, 900.0, null);
            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(legacy, GuidB));

            // Null recording is never a mismatch (defensive, mirrors the picker's null skip).
            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(null, GuidB));
        }

        [Fact]
        public void Predicate_GuidComparisonIsFormatInsensitive()
        {
            // NormalizeGuid canonicalizes to "N" form, so a dashed / braced / upper-case
            // spelling of the SAME launch must not read as a different launch. A false
            // "conclusive" here would drop the correct candidate.
            string dashed = new Guid(GuidA).ToString("D").ToUpperInvariant();
            var recA = Rec("rec-a", "Hopper", 100.0, 900.0, GuidA);

            Assert.False(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(recA, dashed));
            Assert.True(LedgerOrchestrator.IsConclusiveLaunchGuidMismatch(
                recA, new Guid(GuidB).ToString("D")));
        }

        // ----------------------------------------------------------------
        // The MONOTONE property
        // ----------------------------------------------------------------

        [Fact]
        public void Filter_IsMonotone_SurvivorsAreAnOrderPreservingSubsetOfTheInput()
        {
            // The property that makes this the safe half of the fix: the filter can only
            // REMOVE candidates. It never adds one and never reorders one, so the tier walk
            // that runs after it sees a subsequence of exactly what it saw before - a pick
            // that was already correct cannot be turned into a different wrong pick.
            var input = new List<Recording>
            {
                Rec("r0", "Hopper", 100.0, 200.0, GuidA),   // dropped
                Rec("r1", "Hopper", 300.0, 400.0, GuidB),   // kept
                Rec("r2", "Hopper", 500.0, 600.0, null),    // kept (unknown => not conclusive)
                Rec("r3", "Hopper", 700.0, 800.0, GuidA),   // dropped
                Rec("r4", "Hopper", 900.0, 1000.0, GuidB),  // kept
            };

            var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                input, GuidB, out int dropped);

            Assert.Equal(2, dropped);
            Assert.Equal(new[] { "r1", "r2", "r4" }, survivors.Select(r => r.RecordingId).ToArray());

            // Subset: every survivor is the SAME object instance from the input.
            foreach (var s in survivors)
                Assert.Contains(input, r => ReferenceEquals(r, s));

            // Order preserved: survivor indices are strictly increasing in the input.
            var indices = survivors.Select(s => input.FindIndex(r => ReferenceEquals(r, s))).ToArray();
            for (int i = 1; i < indices.Length; i++)
                Assert.True(indices[i] > indices[i - 1]);

            // Count identity: dropped + survivors == input.
            Assert.Equal(input.Count, dropped + survivors.Count);
        }

        [Fact]
        public void Filter_UnknownLiveGuid_IsAnExactNoOp()
        {
            // The documented degradation. Every candidate survives, in order, and nothing is
            // counted as dropped - so no legacy recording loses its correlation.
            var input = new List<Recording>
            {
                Rec("r0", "Hopper", 100.0, 200.0, GuidA),
                Rec("r1", "Hopper", 300.0, 400.0, GuidB),
                Rec("r2", "Hopper", 500.0, 600.0, null),
            };

            foreach (string liveGuid in new[] { null, "" })
            {
                var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                    input, liveGuid, out int dropped);
                Assert.Equal(0, dropped);
                Assert.Equal(
                    input.Select(r => r.RecordingId).ToArray(),
                    survivors.Select(r => r.RecordingId).ToArray());
            }
        }

        [Fact]
        public void Filter_NullInput_YieldsEmptySetAndNoDrops()
        {
            var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                null, GuidA, out int dropped);
            Assert.Empty(survivors);
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void Filter_ReFlyProvisionalShapeSurvives()
        {
            // RewindInvoker.BuildProvisionalRecording leaves RecordedVesselGuid null, and
            // CopyInheritedIdentityForFork later inherits the ORIGIN's guid (the fork restores
            // from a quicksave preserving the origin's Vessel.id). Both shapes are
            // non-conclusive, so the session-aware NotCommitted rule the picker already
            // carries is untouched by this filter - no exemption needed, by construction.
            var freshProvisional = Rec("rec-prov", "Reusable", 1000.0, 5000.0, null);
            freshProvisional.MergeState = MergeState.NotCommitted;
            var inheritedProvisional = Rec("rec-prov-2", "Reusable", 1000.0, 5000.0, GuidA);
            inheritedProvisional.MergeState = MergeState.NotCommitted;

            var survivors = LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid(
                new List<Recording> { freshProvisional, inheritedProvisional },
                GuidA,
                out int dropped);

            Assert.Equal(0, dropped);
            Assert.Equal(2, survivors.Count);
        }

        // ----------------------------------------------------------------
        // The picker: the two-launches-same-name shape
        // ----------------------------------------------------------------

        [Fact]
        public void Picker_TwoLaunchesSameName_DropsTheOtherLaunchAndPicksTheRecoveredOne()
        {
            // THE SHAPE THE ENTRY FILED. Two launches of one craft name:
            //   launch A - an earlier launch still recorded as spanning the recovery moment
            //              (a long-lived sibling: station, stranded stage, drifted EndUT),
            //   launch B - the hop actually being recovered at ut=1000.
            // Both name-match. Without the filter, tier 1 (bracketing, largest EndUT) picks
            // A - the WRONG launch, and on the XP leg that row is irreversible. The filter
            // removes A because its recorded launch guid conclusively differs from the
            // recovering vessel's, and the SAME tier walk then lands on B.
            AddRec("rec-launch-A", "Hopper", 100.0, 5000.0, GuidA);
            AddRec("rec-launch-B", "Hopper", 950.0, 1000.0, GuidB);

            // Negative control, measured rather than asserted from theory: with no live guid
            // the picker still makes the wrong pick. This is the defect, reproduced.
            Assert.Equal(
                "rec-launch-A",
                LedgerOrchestrator.PickRecoveryRecordingId("Hopper", 1000.0));

            // With the recovering vessel's launch guid, A drops out of the candidate set.
            string pick = LedgerOrchestrator.PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);
            Assert.Equal("rec-launch-B", pick);

            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter") &&
                l.Contains("dropped=1") &&
                l.Contains("remaining=1") &&
                l.Contains("reason=guid-conclusive-mismatch"));
        }

        [Fact]
        public void Picker_SingleLaunch_LandsOnTheSamePickAsBeforeTheFilter()
        {
            // NON-REGRESSION. Every committed career fixture flies ONE launch of one craft
            // name, and this is the tier the driven career recovery measurably lands on
            // (tier=most-recent-ended). The filter must be invisible there.
            AddRec("rec-early", "Jumping Flea", 100.0, 300.0, GuidA);
            AddRec("rec-recovered", "Jumping Flea", 320.0, 347.1, GuidA);

            string before = LedgerOrchestrator.PickRecoveryRecordingId("Jumping Flea", 347.5);
            string after = LedgerOrchestrator.PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA), 347.5);

            Assert.Equal("rec-recovered", before);
            Assert.Equal(before, after);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=guid-conclusive-mismatch"));

            // The filter ran and AGREED - a distinct log state from "the filter never ran",
            // which is what a live-proof run reads to know it was active on this leg.
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter") &&
                l.Contains("reason=no-conclusive-mismatch"));
        }

        [Fact]
        public void Picker_LegacyRecordingWithNoRecordedGuid_KeepsItsCorrelation()
        {
            // The common case the stricter positive-identity rule would have broken: a
            // recording captured before RecordedVesselGuid existed (and un-backfillable).
            // A known LIVE guid must not retire its correlation.
            AddRec("rec-legacy", "Old Timer", 100.0, 900.0, null);

            string pick = LedgerOrchestrator.PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName("Old Timer", GuidB), 1000.0);

            Assert.Equal("rec-legacy", pick);
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter") &&
                l.Contains("dropped=0") &&
                l.Contains("reason=no-conclusive-mismatch"));
        }

        [Fact]
        public void Picker_LiveGuidUnknown_LogsTheDegradationAndKeepsTheHistoricalPick()
        {
            AddRec("rec-only", "Hopper", 100.0, 900.0, GuidA);

            string pick = LedgerOrchestrator.PickRecoveryRecordingId("Hopper", 1000.0);

            Assert.Equal("rec-only", pick);
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter") &&
                l.Contains("reason=live-launch-guid-unknown"));
        }

        [Fact]
        public void Picker_EveryCandidateIsADifferentLaunch_ReturnsNullRatherThanAWrongPick()
        {
            // The filter can empty the candidate set. That routes into the picker's EXISTING
            // no-candidate result (null), which the XP leg turns into its
            // reason=no-recovery-recording refusal - the pre-existing fail-safe, reached by a
            // new road. A missing row strictly dominates an irreversible wrong one.
            AddRec("rec-other-launch", "Hopper", 100.0, 5000.0, GuidA);

            string pick = LedgerOrchestrator.PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Null(pick);
            Assert.Contains(logLines, l =>
                l.Contains("PickRecoveryRecordingId guid filter") &&
                l.Contains("dropped=1") &&
                l.Contains("remaining=0"));
        }

        [Fact]
        public void Picker_FilterRunsBeforeTierSelection_NotAfter()
        {
            // Order matters: filtering AFTER the tier walk would let a dropped candidate win
            // its tier and then vanish, refusing a recovery that has a perfectly good owner.
            // Here A wins tier 1 (bracketing) and B only tier 2 (most-recent-ended); the
            // filtered pick must be B, not null.
            AddRec("rec-launch-A", "Hopper", 100.0, 5000.0, GuidA);
            AddRec("rec-launch-B", "Hopper", 200.0, 900.0, GuidB);

            Assert.Equal(
                "rec-launch-B",
                LedgerOrchestrator.PickRecoveryRecordingId(
                    RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0));
        }

        // ----------------------------------------------------------------
        // The three legs share the picker - the XP leg end to end
        // ----------------------------------------------------------------

        [Fact]
        public void XpLeg_ScopesTheRowToTheRecoveredLaunch_NotTheNameSibling()
        {
            // The XP row is the irreversible one, so prove the filter reaches it through the
            // real entry point rather than only through the picker.
            AddRec("rec-launch-A", "Hopper", 100.0, 5000.0, GuidA);
            AddRec("rec-launch-B", "Hopper", 950.0, 1000.0, GuidB);

            var events = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) };
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                events, RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Equal(1, rows);
            var xp = Ledger.Actions.Single(a => a.Type == GameActionType.KerbalExperience);
            Assert.Equal("rec-launch-B", xp.RecordingId);
        }

        [Fact]
        public void XpLeg_FilterEmptiesTheSet_RefusesWithTheExistingFailSafe()
        {
            AddRec("rec-other-launch", "Hopper", 100.0, 5000.0, GuidA);

            var events = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0) };
            int rows = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(
                events, RecoveredVesselIdentity.FromRawName("Hopper", GuidB), 1000.0);

            Assert.Equal(0, rows);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalExperience);
            Assert.Contains(logLines, l =>
                l.Contains("Recovery kerbal XP refused") &&
                l.Contains("reason=no-recovery-recording"));
        }

        // ----------------------------------------------------------------
        // The identity struct carries the guid without disturbing name matching
        // ----------------------------------------------------------------

        [Fact]
        public void Identity_LaunchGuidIsNormalizedAndIsNotPartOfNameMatchingOrTheLogSurface()
        {
            var withGuid = RecoveredVesselIdentity.FromRawName(
                "Hopper", new Guid(GuidA).ToString("D").ToUpperInvariant());
            var withoutGuid = RecoveredVesselIdentity.FromRawName("Hopper");

            // Normalized to canonical "N" form on the way in.
            Assert.True(withGuid.HasLaunchGuid);
            Assert.Equal(GuidA, withGuid.LaunchGuid);
            Assert.False(withoutGuid.HasLaunchGuid);
            Assert.Null(withoutGuid.LaunchGuid);

            // Name matching and the pinned log surface are untouched: two identities that
            // differ ONLY by launch guid still match each other by name, and FormatForLog
            // renders identically (the harness pins that string).
            Assert.True(withGuid.Matches(withoutGuid));
            Assert.True(withGuid.MatchesName("Hopper"));
            Assert.Equal(withoutGuid.FormatForLog(), withGuid.FormatForLog());
        }
    }
}
