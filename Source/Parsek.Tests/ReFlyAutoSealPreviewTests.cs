using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the pre-transition merge dialog auto-seal preview
    /// (<see cref="ReFlyAutoSealPreviewer"/>). The preview is a read-only,
    /// conservative subset of the production classifier in
    /// <see cref="SupersedeCommit"/> - mirrors the gates that are reliable
    /// from live state (Ledger.Actions, tree topology, live
    /// <see cref="Vessel.Situations"/>) and surfaces them as
    /// player-attributable reasons for the dialog body.
    ///
    /// <para>The live <see cref="Vessel"/> branches require Unity runtime
    /// (vessel/orbit/body lookups via <see cref="FlightGlobals"/>); those
    /// rows in the plan's matrix are exercised via in-game tests rather
    /// than xUnit. These tests cover the null-vessel paths plus the
    /// Ledger / tree-topology / format / state-version invariance
    /// branches, which are reachable without Unity.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyAutoSealPreviewTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyAutoSealPreviewTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            Ledger.ResetForTesting();
        }

        // ---------- Helpers ---------------------------------------------

        private const string TreeId = "tree-test";
        private const string ProvisionalId = "rec-provisional";

        private static Recording MakeRecording(
            string id = ProvisionalId, string treeId = TreeId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "TestVessel-" + id,
                TreeId = treeId,
            };
        }

        private static ReFlySessionMarker MakeMarker(
            string treeId = TreeId,
            string activeReFlyRecordingId = ProvisionalId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess-test",
                TreeId = treeId,
                ActiveReFlyRecordingId = activeReFlyRecordingId,
                OriginChildRecordingId = activeReFlyRecordingId,
                RewindPointId = "rp-test",
                InvokedUT = 100.0,
                PreSessionBranchPointIds = new List<string>(),
            };
        }

        private static ParsekScenario MakeScenario(
            ReFlySessionMarker marker = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static GameAction MakeScienceEarning(
            string recordingId,
            ScienceMethod method,
            string subjectId = "crewReport@KerbinSrfLandedShores",
            float scienceAwarded = 5f)
        {
            return new GameAction
            {
                Type = GameActionType.ScienceEarning,
                RecordingId = recordingId,
                UT = 200.0,
                SubjectId = subjectId,
                ExperimentId = "crewReport",
                Body = "Kerbin",
                Situation = "SrfLanded",
                Biome = "Shores",
                ScienceAwarded = scienceAwarded,
                Method = method,
                ActionId = "act-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            };
        }

        // ---------- Null guards -----------------------------------------

        [Fact]
        public void Preview_NullMarker_ReturnsNoSeal()
        {
            var rec = MakeRecording();
            var result = ReFlyAutoSealPreviewer.Preview(rec, null, null);
            Assert.False(result.WillAutoSeal);
            Assert.Empty(result.Reasons);
            Assert.Null(result.FormatHumanReadable());
        }

        [Fact]
        public void Preview_NullProvisional_ReturnsNoSeal()
        {
            var marker = MakeMarker();
            var result = ReFlyAutoSealPreviewer.Preview(null, marker, null);
            Assert.False(result.WillAutoSeal);
        }

        [Fact]
        public void Preview_EmptyMarkerTreeId_ReturnsNoSeal()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            marker.TreeId = null;
            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);
            Assert.False(result.WillAutoSeal);
        }

        [Fact]
        public void Preview_EmptyProvisionalTreeId_ReturnsNoSeal()
        {
            var rec = MakeRecording();
            rec.TreeId = null;
            var marker = MakeMarker();
            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);
            Assert.False(result.WillAutoSeal);
        }

        [Fact]
        public void Preview_TreeIdMismatch_ReturnsNoSeal()
        {
            var rec = MakeRecording(treeId: "tree-A");
            var marker = MakeMarker(treeId: "tree-B");
            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);
            Assert.False(result.WillAutoSeal);
        }

        [Fact]
        public void Preview_RecordingIdMismatchSameTree_StillRunsOtherChecks()
        {
            // In-place continuation has provisional.RecordingId !=
            // marker.ActiveReFlyRecordingId but same TreeId. The preview
            // gates on TreeId only - RecordingId mismatch must NOT
            // disqualify (otherwise in-place continuation would never
            // surface a seal reason). Confirm by adding a science action
            // tagged on the provisional and asserting it's surfaced.
            var rec = MakeRecording(id: "rec-other-active");   // different from marker.ActiveReFlyRecordingId
            var marker = MakeMarker();   // ActiveReFlyRecordingId == ProvisionalId
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(
                "rec-other-active", ScienceMethod.Transmitted));
            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);
            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.TransmittedScience, result.Reasons);
        }

        // ---------- Earned-science branch -------------------------------

        [Fact]
        public void Preview_TransmittedScience_AddsTransmittedReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Transmitted));

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Equal(new[] { ReFlyAutoSealReason.TransmittedScience }, result.Reasons);
            Assert.Equal("transmitted science", result.FormatHumanReadable());
        }

        [Fact]
        public void Preview_RecoveredScience_AddsRecoveredReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Recovered));

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Equal(new[] { ReFlyAutoSealReason.RecoveredScience }, result.Reasons);
            Assert.Equal("recovered science", result.FormatHumanReadable());
        }

        [Fact]
        public void Preview_TwoTransmittedRows_DedupesToOneReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Transmitted, subjectId: "subj-A"));
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Transmitted, subjectId: "subj-B"));

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Single(result.Reasons);
            Assert.Equal(ReFlyAutoSealReason.TransmittedScience, result.Reasons[0]);
        }

        [Fact]
        public void Preview_MixedTransmittedAndRecovered_BothReasons()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Transmitted));
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Recovered));

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Equal(new[]
            {
                ReFlyAutoSealReason.TransmittedScience,
                ReFlyAutoSealReason.RecoveredScience,
            }, result.Reasons);
            Assert.Equal("transmitted science and recovered science",
                result.FormatHumanReadable());
        }

        [Fact]
        public void Preview_ScienceTaggedOnDifferentRecording_NoReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            // Tag science on a recording NOT in the lineage.
            Ledger.AddAction(MakeScienceEarning("rec-unrelated", ScienceMethod.Transmitted));

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.False(result.WillAutoSeal);
            Assert.Empty(result.Reasons);
        }

        [Fact]
        public void Preview_NonScienceActionTaggedOnProvisional_NoReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            // FundsEarning is world-state-changing but not retry-blocking
            // (filtered by IsRetryBlockingRecordingAction at SupersedeCommit.cs:1289).
            // The preview filters non-ScienceEarning types by the same logic.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                RecordingId = rec.RecordingId,
                UT = 200.0,
                FundsAwarded = 1000f,
                ActionId = "act-funds",
            });

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.False(result.WillAutoSeal);
        }

        [Fact]
        public void Preview_EmptyLedger_NoScienceReason()
        {
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.False(result.WillAutoSeal);
        }

        // ---------- State-version invariance ----------------------------

        [Fact]
        public void Preview_DoesNotMutateLedger()
        {
            // Read-only contract: Preview must not mutate Ledger.Actions
            // even when it walks them. Pin the contract by snapshotting
            // counts and comparing.
            var rec = MakeRecording();
            var marker = MakeMarker();
            MakeScenario(marker);
            Ledger.AddAction(MakeScienceEarning(rec.RecordingId, ScienceMethod.Transmitted));
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                RecordingId = rec.RecordingId,
                UT = 250.0,
                FundsAwarded = 500f,
                ActionId = "act-funds",
            });
            int beforeCount = Ledger.Actions.Count;

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Equal(beforeCount, Ledger.Actions.Count);
        }

        // ---------- Recorded-terminal classifier ------------------------
        //
        // Deferred merge fallback (ShowTreeDialog 1-arg overload) can fire
        // in Space Center / Tracking Station with FlightGlobals.ActiveVessel
        // null. The recorded-terminal classifier must surface the seal
        // reason from Recording.TerminalStateValue so the dialog still
        // warns "This cannot be undone" when production would seal.

        [Fact]
        public void Preview_RecordedTerminalLanded_NullVessel_FlagsLanded()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Landed;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.Landed, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalSplashed_NullVessel_FlagsSplashedDown()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Splashed;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.SplashedDown, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalOrbiting_NullVessel_FlagsStableOrbit()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Orbiting;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.StableOrbit, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalSubOrbital_NullVessel_FlagsSubOrbitalArc()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.SubOrbital;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.SubOrbitalArc, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalDocked_NullVessel_FlagsDocked()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Docked;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.DockedWithAnother, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalRecovered_NullVessel_FlagsRecovered()
        {
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Recovered;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.VesselRecovered, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalBoarded_NullVessel_FlagsBoarded()
        {
            // Production seals on Boarded via IsHardSafetyTerminal
            // (SupersedeCommit:1052-1062). The structural classifier excludes
            // BranchPointType.Board, and any upstream EVA BP that produced
            // the kerbal recording typically sits in
            // marker.PreSessionBranchPointIds (skipped by the structural
            // scan). The recorded-terminal classifier is the only path that
            // surfaces this seal reason in the preview.
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Boarded;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.True(result.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.KerbalBoarded, result.Reasons);
        }

        [Fact]
        public void Preview_RecordedTerminalDestroyed_DoesNotFlag()
        {
            // Destroyed routes through the "crashed" classifier reason and
            // does NOT auto-seal; the Re-Fly retry-on-crash flow takes over.
            // Preview must not surface a seal reason here.
            var rec = MakeRecording();
            rec.TerminalStateValue = TerminalState.Destroyed;
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.False(result.WillAutoSeal);
            Assert.Empty(result.Reasons);
        }

        [Fact]
        public void Preview_NoTerminalSet_DoesNotFlagFromRecordedPath()
        {
            // Pre-transition: recording not yet finalized, TerminalStateValue
            // is null. Recorded-terminal classifier must skip; only the
            // live-vessel proxy (not exercised here, null vessel) would
            // contribute terminal reasons.
            var rec = MakeRecording();
            Assert.Null(rec.TerminalStateValue);
            var marker = MakeMarker();
            MakeScenario(marker);

            var result = ReFlyAutoSealPreviewer.Preview(rec, marker, null);

            Assert.False(result.WillAutoSeal);
        }

        // ---------- FormatHumanReadable standalone ----------------------

        [Fact]
        public void Phrase_AllReasons_MatchSpec()
        {
            var expected = new Dictionary<ReFlyAutoSealReason, string>
            {
                { ReFlyAutoSealReason.EarnedScience, "earned science" },
                { ReFlyAutoSealReason.TransmittedScience, "transmitted science" },
                { ReFlyAutoSealReason.RecoveredScience, "recovered science" },
                { ReFlyAutoSealReason.Undocked, "undocked" },
                { ReFlyAutoSealReason.KerbalEva, "sent a kerbal on EVA" },
                { ReFlyAutoSealReason.PartBrokeOff, "broke off a part" },
                { ReFlyAutoSealReason.VesselBrokeUp, "the vessel broke up" },
                { ReFlyAutoSealReason.DockedWithAnother, "docked with another vessel" },
                { ReFlyAutoSealReason.VesselRecovered, "the vessel was recovered" },
                { ReFlyAutoSealReason.KerbalBoarded, "the kerbal boarded another vessel" },
                { ReFlyAutoSealReason.Landed, "landed" },
                { ReFlyAutoSealReason.SplashedDown, "splashed down" },
                { ReFlyAutoSealReason.StableOrbit, "reached a stable orbit" },
                { ReFlyAutoSealReason.SubOrbitalArc, "reached a sub-orbital arc" },
            };
            foreach (var kvp in expected)
                Assert.Equal(kvp.Value, ReFlyAutoSealPreviewResult.PhraseFor(kvp.Key));
        }

        [Fact]
        public void Format_EmptyReasons_ReturnsNull()
        {
            var result = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = false,
                Reasons = new List<ReFlyAutoSealReason>(),
            };
            Assert.Null(result.FormatHumanReadable());
        }

        [Fact]
        public void Format_SingleReason_BarePhrase()
        {
            var result = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                    { ReFlyAutoSealReason.TransmittedScience },
            };
            Assert.Equal("transmitted science", result.FormatHumanReadable());
        }

        [Fact]
        public void Format_TwoReasons_AndJoiner()
        {
            var result = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                {
                    ReFlyAutoSealReason.Undocked,
                    ReFlyAutoSealReason.KerbalEva,
                },
            };
            Assert.Equal("undocked and sent a kerbal on EVA",
                result.FormatHumanReadable());
        }

        [Fact]
        public void Format_ThreeReasons_OxfordComma()
        {
            var result = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                {
                    ReFlyAutoSealReason.TransmittedScience,
                    ReFlyAutoSealReason.Undocked,
                    ReFlyAutoSealReason.DockedWithAnother,
                },
            };
            Assert.Equal(
                "transmitted science, undocked, and docked with another vessel",
                result.FormatHumanReadable());
        }

        [Fact]
        public void Format_MixedSubjectPhrases_StillReadsCleanly()
        {
            // "the vessel broke up" is subject-led; under the colon-list
            // form ("for the following reason(s): the vessel broke up
            // and docked with another vessel.") the implicit subject of
            // the second clause is "the vessel" too, which is fine.
            var result = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                {
                    ReFlyAutoSealReason.VesselBrokeUp,
                    ReFlyAutoSealReason.DockedWithAnother,
                },
            };
            Assert.Equal(
                "the vessel broke up and docked with another vessel",
                result.FormatHumanReadable());
        }

        // ---------- BuildReFlyDialogBody --------------------------------

        [Fact]
        public void BuildBody_NoSeal_UsesDefaultCopy()
        {
            var preview = ReFlyAutoSealPreviewResult.NoSeal();
            string body = MergeDialog.BuildReFlyDialogBody(
                "TestVessel", 123.0, preview);
            Assert.Contains("TestVessel", body);
            Assert.Contains("Do you want to commit this Re-Fly attempt", body);
            Assert.Contains("to the timeline", body);
            Assert.DoesNotContain("cannot be undone", body);
            Assert.DoesNotContain("auto-sealed", body);
            Assert.DoesNotContain("for the following reason", body);
        }

        [Fact]
        public void BuildBody_AutoSeal_SingleReason_IncludesReasonAndUndoWarning()
        {
            var preview = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                    { ReFlyAutoSealReason.TransmittedScience },
            };
            string body = MergeDialog.BuildReFlyDialogBody(
                "TestVessel", 123.0, preview);
            Assert.Contains("If not discarded, this Re-Fly attempt", body);
            Assert.Contains("merged AND auto-sealed", body);
            Assert.Contains("for the following reason(s): transmitted science.",
                body);
            Assert.Contains("This cannot be undone", body);
            Assert.DoesNotContain("slot will become permanent", body);
            Assert.DoesNotContain("not be able to Re-Fly this line of flight",
                body);
        }

        [Fact]
        public void BuildBody_AutoSeal_MultipleReasons_Composes()
        {
            var preview = new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = true,
                Reasons = new List<ReFlyAutoSealReason>
                {
                    ReFlyAutoSealReason.TransmittedScience,
                    ReFlyAutoSealReason.Undocked,
                    ReFlyAutoSealReason.DockedWithAnother,
                },
            };
            string body = MergeDialog.BuildReFlyDialogBody(
                "TestVessel", 123.0, preview);
            Assert.Contains(
                "for the following reason(s): transmitted science, undocked, " +
                "and docked with another vessel.",
                body);
        }

        [Fact]
        public void BuildBody_HeadlineIncludesVesselNameAndDuration()
        {
            var preview = ReFlyAutoSealPreviewResult.NoSeal();
            string body = MergeDialog.BuildReFlyDialogBody(
                "MyShip", 65.0, preview);
            Assert.Contains("<align=\"center\">MyShip - ", body);
        }

        // ---------- ShouldUseLiveVesselForReFlyTarget (pid match) -------

        [Fact]
        public void ShouldUseLiveVessel_MatchingPids_ReturnsTrue()
        {
            // Re-Fly recording's pid matches the live active vessel's pid:
            // the live-vessel terminal branch is safe to run.
            Assert.True(ReFlyAutoSealPreviewer.ShouldUseLiveVesselForReFlyTarget(
                candidatePid: 12345u, expectedPid: 12345u));
        }

        [Fact]
        public void ShouldUseLiveVessel_DifferentPids_ReturnsFalse()
        {
            // Active vessel has been switched mid-Re-Fly; live-terminal
            // reasons would describe an unrelated vessel.
            Assert.False(ReFlyAutoSealPreviewer.ShouldUseLiveVesselForReFlyTarget(
                candidatePid: 12345u, expectedPid: 67890u));
        }

        [Fact]
        public void ShouldUseLiveVessel_ZeroCandidatePid_ReturnsFalse()
        {
            // pid==0 is the unset sentinel; do not accept as match.
            Assert.False(ReFlyAutoSealPreviewer.ShouldUseLiveVesselForReFlyTarget(
                candidatePid: 0u, expectedPid: 12345u));
        }

        [Fact]
        public void ShouldUseLiveVessel_ZeroExpectedPid_ReturnsFalse()
        {
            // The Re-Fly recording's VesselPersistentId is unset (0).
            // Defensive: skip rather than pretend any vessel matches a 0
            // sentinel.
            Assert.False(ReFlyAutoSealPreviewer.ShouldUseLiveVesselForReFlyTarget(
                candidatePid: 12345u, expectedPid: 0u));
        }

        [Fact]
        public void ShouldUseLiveVessel_BothZero_ReturnsFalse()
        {
            Assert.False(ReFlyAutoSealPreviewer.ShouldUseLiveVesselForReFlyTarget(
                candidatePid: 0u, expectedPid: 0u));
        }
    }
}
