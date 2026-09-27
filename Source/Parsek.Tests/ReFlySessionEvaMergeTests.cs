using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-SESSION-EVA-CANNOT-SUPERSEDE (PR #1907 review): a Re-Fly session in which
    /// the player goes EVA from the re-flown vessel must merge like any other session.
    /// The EVA split ends the fork (the NotCommitted provisional the marker names) at
    /// the EVA branch point with no terminal; the vessel's real ending lives on the
    /// continuation the slot walk (<see cref="EffectiveState.WalkSlotVessel"/>) reaches.
    /// Before the fix the merge validated the RAW fork, took
    /// <c>outcome=refused-unflown-provisional</c> ("null TerminalState") and wrote 0
    /// supersede rows, so the old stretch stayed visible next to the new one and the
    /// design section 4.9 EVA auto-seal was unreachable.
    ///
    /// <para>Fixture: <see cref="ReFlyThroughEvaFixture"/>'s Reboard tree (the ORIGINAL
    /// flight: upper stage U -EVA- U1 + Jeb -Board- U2, U2 crashed, slot open) plus a
    /// Re-Fly session of the upper-stage slot
    /// (<see cref="ReFlyThroughEvaFixture.AddReFlySession"/>). The merge runs through the
    /// real <see cref="MergeJournalOrchestrator.RunMerge"/>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlySessionEvaMergeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly List<string> durableSaves = new List<string>();

        public ReFlySessionEvaMergeTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RouteStore.ResetForTesting();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();
            MarkerValidator.ResetTestOverrides();
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            MergeJournalOrchestrator.DurableSaveForTesting = label => durableSaves.Add(label);
            MergeJournalOrchestrator.RecalcAfterMarkerClearedForTesting = _ => { };
        }

        public void Dispose()
        {
            MergeJournalOrchestrator.ResetTestOverrides();
            MarkerValidator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RouteStore.ResetForTesting();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            KspStatePatcher.ResetForTesting();
        }

        // ------------------------------------------------------------------
        // Fixture plumbing
        // ------------------------------------------------------------------

        private static readonly string[] OldStretchIds =
        {
            ReFlyThroughEvaFixture.UpperId,
            ReFlyThroughEvaFixture.UpperAfterEvaId,
            ReFlyThroughEvaFixture.KerbalId,
            ReFlyThroughEvaFixture.UpperAfterBoardId,
        };

        private ParsekScenario Install(
            ReFlySessionShape shape, TerminalState vesselEnd, out Recording fork)
        {
            RecordingTree tree = ReFlyThroughEvaFixture.MaterializeTree(ReFlyThroughEvaVariant.Reboard);
            // The original flight's slot is open: its walked tip (U2, crashed) was
            // promoted by Site A when the tree first committed.
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId].MergeState =
                MergeState.CommittedProvisional;
            fork = ReFlyThroughEvaFixture.AddReFlySession(tree, shape, vesselEnd);
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { ReFlyThroughEvaFixture.BuildRewindPoint() },
                ActiveReFlySessionMarker = ReFlyThroughEvaFixture.BuildSessionMarker(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static Recording Rec(string id) => EffectiveState.FindCommittedRecordingByIdRaw(id);

        private static HashSet<string> OldIds(ParsekScenario scenario)
            => new HashSet<string>(
                scenario.RecordingSupersedes.Select(r => r.OldRecordingId), StringComparer.Ordinal);

        private static string SlotTipId(ParsekScenario scenario)
            => EffectiveState.EffectiveTipRecordingId(
                ReFlyThroughEvaFixture.UpperId, scenario.RecordingSupersedes);

        private void AssertOldStretchSupersededByFork(ParsekScenario scenario)
        {
            var oldIds = OldIds(scenario);
            foreach (string id in OldStretchIds)
                Assert.Contains(id, oldIds);
            Assert.All(scenario.RecordingSupersedes,
                r => Assert.Equal(ReFlyThroughEvaFixture.ForkId, r.NewRecordingId));
            Assert.DoesNotContain(logLines, l => l.Contains("outcome=refused-unflown-provisional"));
        }

        private void AssertSessionRecordingsNeverSuperseded(ParsekScenario scenario)
        {
            var oldIds = OldIds(scenario);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterEvaId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.SessionKerbalId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterBoardId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, oldIds);
        }

        // ------------------------------------------------------------------
        // The bug: EVA + re-board during the session
        // ------------------------------------------------------------------

        [Fact]
        public void Merge_EvaAndReboard_SupersedesTheOldStretch_AndTheEvaAutoSeals()
        {
            // Red on the old code: 0 rows, refused-unflown-provisional. SubOrbital so the
            // stable-terminal focus seal cannot fire and the ONLY seal trigger is the
            // session's structural EVA (design section 4.9 rule 2).
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.SubOrbital, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            AssertSessionRecordingsNeverSuperseded(scenario);
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("outcome=validated-through-vessel-walk")
                && l.Contains("provisional=" + ReFlyThroughEvaFixture.ForkId)
                && l.Contains("terminalRec=" + ReFlyThroughEvaFixture.ForkAfterBoardId)
                && l.Contains("terminal=SubOrbital")
                && l.Contains("evaHops=1") && l.Contains("boardHops=1"));

            // The slot's tip is the re-flown vessel's REAL tip, and it is sealed.
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterBoardId, SlotTipId(scenario));
            Assert.Equal(MergeState.Immutable, fork.MergeState);
            Assert.Equal(MergeState.Immutable, Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).MergeState);
            Assert.Contains(logLines, l => l.Contains("Auto-sealed re-fly slot=")
                && l.Contains("reason=structuralMutation:")
                && l.Contains("firstType=EVA"));
            Assert.True(fork.PlaybackEnabled);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
        }

        [Fact]
        public void Merge_EvaAndReboard_ToOrbit_SealsOnTheFocusSlotVerdictReadOnTheWalkedTip()
        {
            // The live lane's shape (RF-19): the re-flown stack reaches orbit, the crew
            // steps out and climbs back in, the merge concludes. The focus-slot override
            // reads Orbiting on the walked tip and seals.
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.Orbiting, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            AssertSessionRecordingsNeverSuperseded(scenario);
            Assert.Equal(MergeState.Immutable, fork.MergeState);
            Assert.Equal(MergeState.Immutable, Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).MergeState);
            Assert.Contains(logLines, l => l.Contains("Auto-sealed re-fly slot=")
                && l.Contains("terminal=Orbiting")
                && l.Contains("reason=classifierClosed:stableTerminalFocusSlot"));
        }

        [Fact]
        public void Merge_EvaAndReboard_ThenCrash_Supersedes_AndKeepsTheSlotOpenForRetry()
        {
            // Red on the old code (0 rows). The documented retry rule: a Destroyed outcome
            // keeps the slot open even though the session EVA'd. Open/closed is read from
            // the walked tip, so the tip - not only the fork - must carry the open state.
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.Destroyed, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterBoardId, SlotTipId(scenario));
            Assert.Equal(MergeState.CommittedProvisional, fork.MergeState);
            Assert.Equal(MergeState.CommittedProvisional,
                Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).MergeState);
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("session vessel tip rec=" + ReFlyThroughEvaFixture.ForkAfterBoardId)
                && l.Contains("Immutable->CommittedProvisional"));
            Assert.DoesNotContain(logLines, l => l.Contains("Auto-sealed re-fly slot="));
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(
                scenario.RewindPoints[0].ChildSlots[ReFlyThroughEvaFixture.UpperSlotIndex]));
        }

        [Fact]
        public void Merge_KerbalLeftOnEva_SupersedesAndSealsOnTheVesselContinuation()
        {
            var scenario = Install(ReFlySessionShape.EvaKerbalStaysOut, TerminalState.Landed, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterEvaId, SlotTipId(scenario));
            Assert.Equal(MergeState.Immutable, Rec(ReFlyThroughEvaFixture.ForkAfterEvaId).MergeState);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.SessionKerbalId, OldIds(scenario));
        }

        [Fact]
        public void Merge_KerbalBoardsForeignVessel_SupersedesAndSeals_EvenOnACrash()
        {
            // Rule chosen for owner ruling 2026-09-27 rule 2 (a kerbal who left for a
            // foreign vessel makes the slot non-re-flyable) at MERGE time: the session
            // still supersedes the old stretch (the player keeps the flight they flew),
            // and the slot seals - the foreign join wins over the crash retry, because
            // re-flying the slot again could not undo the kerbal's life aboard the other
            // vessel.
            var scenario = Install(
                ReFlySessionShape.EvaKerbalBoardsForeignVessel, TerminalState.Destroyed, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForeignVesselId, OldIds(scenario));
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForeignVesselAfterBoardId, OldIds(scenario));
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterEvaId, SlotTipId(scenario));
            Assert.Equal(MergeState.Immutable, fork.MergeState);
            Assert.Equal(MergeState.Immutable, Rec(ReFlyThroughEvaFixture.ForkAfterEvaId).MergeState);
            Assert.Contains(logLines, l => l.Contains("Auto-sealed re-fly slot=")
                && l.Contains(UnfinishedFlightClassifier.EvaCrewJoinedForeignVesselReason));
        }

        [Fact]
        public void Closure_FencesTheSessionsOwnContinuations_ThatArePidPeersOfTheOldStretch()
        {
            // The session's vessel continuations carry the re-flown vessel's pid and start
            // after the rewind point - exactly the pid-peer gate. Before the fence they
            // entered the closure and the merge would supersede the new flight with its
            // own head (hidden until now behind the refusal, which wrote no rows).
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.Landed, out _);
            var marker = scenario.ActiveReFlySessionMarker;

            var closure = EffectiveState.ComputeSubtreeClosureInternal(marker, marker.SupersedeTargetId);

            foreach (string id in OldStretchIds)
                Assert.Contains(id, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterEvaId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.SessionKerbalId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterBoardId, closure);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("fenced live session's own recordings")
                && l.Contains("provisional=" + ReFlyThroughEvaFixture.ForkId)
                && l.Contains("count=3"));
        }

        [Fact]
        public void SessionOwned_LegacyMarkerNamingTheOrigin_FencesNothing()
        {
            Install(ReFlySessionShape.EvaReboard, TerminalState.Landed, out _);
            var recById = RecordingStore.CommittedRecordings.ToDictionary(
                r => r.RecordingId, r => r, StringComparer.Ordinal);
            var legacy = ReFlyThroughEvaFixture.BuildSessionMarker();
            legacy.ActiveReFlyRecordingId = legacy.OriginChildRecordingId;

            Assert.Null(EffectiveState.CollectActiveSessionOwnedRecordingIds(
                legacy, legacy.SupersedeTargetId, recById));
            Assert.Null(EffectiveState.CollectActiveSessionOwnedRecordingIds(
                null, "x", recById));

            var owned = EffectiveState.CollectActiveSessionOwnedRecordingIds(
                ReFlyThroughEvaFixture.BuildSessionMarker(),
                ReFlyThroughEvaFixture.UpperAfterBoardId, recById);
            // The fork's descendants; the fork itself is fenced by the closure's own
            // NotCommitted guards.
            Assert.Equal(3, owned.Count);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkId, owned);
        }

        // ------------------------------------------------------------------
        // Crash recovery: the journal re-runs idempotently
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("TreeMerge")]
        [InlineData("Split")]
        [InlineData("Supersede")]
        [InlineData("Finalize")]
        public void CrashMidMerge_FinisherDrivesForward_SameRowsNoDuplicates(string crashAt)
        {
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.Destroyed, out var fork);
            MergeJournalOrchestrator.FaultInjectionPoint =
                (MergeJournalOrchestrator.Phase)Enum.Parse(typeof(MergeJournalOrchestrator.Phase), crashAt);
            Assert.Throws<MergeJournalOrchestrator.FaultInjectionException>(() =>
                MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));
            MergeJournalOrchestrator.FaultInjectionPoint = null;
            Assert.NotNull(scenario.ActiveMergeJournal);

            Assert.True(MergeJournalOrchestrator.RunFinisher());

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            AssertOldStretchSupersededByFork(scenario);
            var pairs = scenario.RecordingSupersedes
                .Select(r => r.OldRecordingId + "->" + r.NewRecordingId).ToList();
            Assert.Equal(pairs.Count, pairs.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(OldStretchIds.Length, pairs.Count);
            Assert.Equal(MergeState.CommittedProvisional, fork.MergeState);
            Assert.Equal(MergeState.CommittedProvisional,
                Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).MergeState);
        }

        // ------------------------------------------------------------------
        // Mid-session load: the marker keeps naming the fork, and it stays valid
        // ------------------------------------------------------------------

        [Fact]
        public void MidSession_AfterTheEva_MarkerStaysValid_AndTheSweepKeepsTheSession()
        {
            // Why the fix resolves the walk at merge time instead of re-pointing
            // ActiveReFlyRecordingId at the continuation: MarkerValidator requires the
            // marker's recording to be the NotCommitted provisional. The continuation is
            // an ordinary Immutable recording, so a re-pointed marker would be discarded
            // on the next load (F5 / F9 mid-session).
            var scenario = Install(ReFlySessionShape.EvaReboard, TerminalState.SubOrbital, out var fork);
            MarkerValidator.NowUtProvider = () => ReFlyThroughEvaFixture.SessionEndUT;

            MarkerValidationResult result = MarkerValidator.Validate(scenario.ActiveReFlySessionMarker);
            Assert.True(result.Valid, result.Reason + " " + result.Details);

            var repointed = ReFlyThroughEvaFixture.BuildSessionMarker();
            repointed.ActiveReFlyRecordingId = ReFlyThroughEvaFixture.ForkAfterBoardId;
            Assert.False(MarkerValidator.Validate(repointed).Valid);

            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal(ReFlyThroughEvaFixture.ForkId, scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId);
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkId));
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkAfterEvaId));
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.SessionKerbalId));
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkAfterBoardId));
        }

        // ------------------------------------------------------------------
        // Mirror directions: sessions that did NOT go EVA
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(ReFlySessionShape.NoBranch)]
        [InlineData(ReFlySessionShape.Dock)]
        [InlineData(ReFlySessionShape.Stage)]
        public void Mirror_NoHopSessions_ValidateOnTheForkItself_Unchanged(ReFlySessionShape shape)
        {
            // Dock closes the fork Docked; staging keeps the fork flying under its own id.
            // Neither hop exists, so the validation reads the fork exactly as before.
            var scenario = Install(shape, TerminalState.Landed, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            Assert.DoesNotContain(logLines, l => l.Contains("outcome=validated-through-vessel-walk"));
            Assert.Equal(ReFlyThroughEvaFixture.ForkId, SlotTipId(scenario));
            Assert.DoesNotContain(logLines, l => l.Contains("session vessel tip rec="));
        }

        [Fact]
        public void Mirror_SwitchContinuation_ValidatesThroughTheSameWalk()
        {
            // Same bug class, same fix: a stock Switch-To away and back splits the fork
            // at a VesselSwitchContinuation point, and the walk has always hopped those.
            var scenario = Install(ReFlySessionShape.SwitchContinuation, TerminalState.Destroyed, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            Assert.Contains(logLines, l => l.Contains("outcome=validated-through-vessel-walk")
                && l.Contains("switchHops=1"));
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterSwitchId, OldIds(scenario));
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterSwitchId, SlotTipId(scenario));
            Assert.Equal(MergeState.CommittedProvisional,
                Rec(ReFlyThroughEvaFixture.ForkAfterSwitchId).MergeState);
        }

        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Destroyed)]
        public void Merge_Undock_SupersedesAndSealsOnTheSessionSeparation(TerminalState survivorEnd)
        {
            // REFLY-SESSION-UNDOCK-CANNOT-SUPERSEDE (owner-approved 2026-09-27). An undock
            // ends the fork at the Undock split with its terminal untouched, and the slot
            // walk stops there (a separation, design section 1.6). The fork concluded at a
            // separation THIS session authored, so it replaces the old stretch, and the
            // section 4.9 close rules seal the slot whatever the halves did afterwards -
            // the survivor's crash is past the slot's end.
            var scenario = Install(ReFlySessionShape.Undock, survivorEnd, out var fork);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            AssertOldStretchSupersededByFork(scenario);
            var oldIds = OldIds(scenario);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkAfterUndockId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.ForkUndockedPartnerId, oldIds);
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("outcome=validated-at-session-separation")
                && l.Contains("provisional=" + ReFlyThroughEvaFixture.ForkId)
                && l.Contains("bp=" + ReFlyThroughEvaFixture.SessionUndockBranchPointId)
                && l.Contains("type=Undock"));
            Assert.Equal(ReFlyThroughEvaFixture.ForkId, SlotTipId(scenario));
            Assert.Equal(MergeState.Immutable, fork.MergeState);
            // Section 4.9 rule 4: the classifier reads the fork (the slot's walked tip)
            // ending at a downstream branch point and closes the slot before the rule 2
            // structural gate is reached; either rule seals, this is the one that fires.
            Assert.Contains(logLines, l => l.Contains("Auto-sealed re-fly slot=")
                && l.Contains("rec=" + ReFlyThroughEvaFixture.ForkId)
                && l.Contains("mergeState=Immutable")
                && l.Contains("reason=classifierClosed:downstreamBp"));
            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(
                scenario.RewindPoints[0].ChildSlots[ReFlyThroughEvaFixture.UpperSlotIndex]));
            Assert.Null(scenario.ActiveReFlySessionMarker);
        }

        [Fact]
        public void Undock_CrashMidMerge_FinisherDrivesForward_SameRows()
        {
            var scenario = Install(ReFlySessionShape.Undock, TerminalState.Landed, out var fork);
            MergeJournalOrchestrator.FaultInjectionPoint = MergeJournalOrchestrator.Phase.Supersede;
            Assert.Throws<MergeJournalOrchestrator.FaultInjectionException>(() =>
                MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));
            MergeJournalOrchestrator.FaultInjectionPoint = null;

            Assert.True(MergeJournalOrchestrator.RunFinisher());

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            AssertOldStretchSupersededByFork(scenario);
            Assert.Equal(OldStretchIds.Length, scenario.RecordingSupersedes.Count);
            Assert.Equal(MergeState.Immutable, fork.MergeState);
        }

        [Fact]
        public void Undock_MidSession_MarkerStaysValid_AndTheSweepKeepsTheSession()
        {
            var scenario = Install(ReFlySessionShape.Undock, TerminalState.Landed, out _);
            MarkerValidator.NowUtProvider = () => ReFlyThroughEvaFixture.SessionEndUT;

            Assert.True(MarkerValidator.Validate(scenario.ActiveReFlySessionMarker).Valid);
            LoadTimeSweep.Run();

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkId));
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkAfterUndockId));
            Assert.NotNull(Rec(ReFlyThroughEvaFixture.ForkUndockedPartnerId));
        }

        [Fact]
        public void Undock_PreSessionBranchPoint_IsNotASessionConclusion_StillRefused()
        {
            // The rule reads the marker's pre-session baseline: a separation that already
            // existed when the session began is not something this re-fly concluded at.
            var scenario = Install(ReFlySessionShape.Undock, TerminalState.Landed, out var fork);
            scenario.ActiveReFlySessionMarker.PreSessionBranchPointIds.Add(
                ReFlyThroughEvaFixture.SessionUndockBranchPointId);

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l => l.Contains("outcome=refused-unflown-provisional")
                && l.Contains("reason=null TerminalState")
                && l.Contains("walkStop=notSwitchBranchPoint"));
        }

        [Fact]
        public void Undock_LegacyMarkerWithoutBaseline_StillRefused()
        {
            var scenario = Install(ReFlySessionShape.Undock, TerminalState.Landed, out var fork);
            scenario.ActiveReFlySessionMarker.PreSessionBranchPointIds = null;

            Assert.True(MergeJournalOrchestrator.RunMerge(scenario.ActiveReFlySessionMarker, fork));

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l => l.Contains("outcome=refused-unflown-provisional"));
        }

        [Fact]
        public void Pure_IsSessionSeparationStop_Gates()
        {
            Install(ReFlySessionShape.Undock, TerminalState.Landed, out var fork);
            var marker = ReFlyThroughEvaFixture.BuildSessionMarker();
            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(fork, null, true, false);
            BranchPoint bp;

            Assert.True(SupersedeCommit.IsSessionSeparationStop(walk, marker, null, out bp));
            Assert.Equal(BranchPointType.Undock, bp.Type);

            Assert.False(SupersedeCommit.IsSessionSeparationStop(walk, null, null, out _));
            Assert.False(SupersedeCommit.IsSessionSeparationStop(null, marker, null, out _));
            var baselined = ReFlyThroughEvaFixture.BuildSessionMarker();
            baselined.PreSessionBranchPointIds.Add(ReFlyThroughEvaFixture.SessionUndockBranchPointId);
            Assert.False(SupersedeCommit.IsSessionSeparationStop(walk, baselined, null, out _));

            // A walk that did not stop at a branch point (the fork simply ended) never counts.
            var noStop = new SlotVesselWalk { Start = fork, Tip = fork };
            Assert.False(SupersedeCommit.IsSessionSeparationStop(noStop, marker, null, out _));

            // Only a separation counts: a foreign Board or a Dock stop does not.
            var stop = new SlotVesselWalk
            {
                Start = fork, Tip = fork,
                StopReason = "notSwitchBranchPoint",
                StopBranchPointId = ReFlyThroughEvaFixture.SessionUndockBranchPointId,
            };
            var tree = EffectiveState.ResolveOwningTree(fork, null);
            var undockBp = EffectiveState.FindBranchPointByIdInTree(
                tree, ReFlyThroughEvaFixture.SessionUndockBranchPointId);
            undockBp.Type = BranchPointType.Board;
            Assert.False(SupersedeCommit.IsSessionSeparationStop(stop, marker, null, out _));
            undockBp.Type = BranchPointType.Dock;
            Assert.False(SupersedeCommit.IsSessionSeparationStop(stop, marker, null, out _));
            undockBp.Type = BranchPointType.JointBreak;
            Assert.True(SupersedeCommit.IsSessionSeparationStop(stop, marker, null, out _));
            undockBp.Type = BranchPointType.Breakup;
            Assert.True(SupersedeCommit.IsSessionSeparationStop(stop, marker, null, out _));

            // The tip must be a parent of the stop branch point.
            undockBp.Type = BranchPointType.Undock;
            var foreignTip = new SlotVesselWalk
            {
                Start = fork, Tip = Rec(ReFlyThroughEvaFixture.UpperId),
                StopReason = "notSwitchBranchPoint",
                StopBranchPointId = ReFlyThroughEvaFixture.SessionUndockBranchPointId,
            };
            Assert.False(SupersedeCommit.IsSessionSeparationStop(foreignTip, marker, null, out _));
        }

        // ------------------------------------------------------------------
        // The pure predicate
        // ------------------------------------------------------------------

        [Fact]
        public void Validate_NoHop_IsTheRawPredicate()
        {
            var scenario = Install(ReFlySessionShape.NoBranch, TerminalState.Landed, out var fork);

            Assert.True(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                fork, null, out string reason, out SlotVesselWalk walk));
            Assert.Null(reason);
            Assert.Same(fork, walk.Tip);

            var empty = new Recording { RecordingId = "unflown", TerminalStateValue = TerminalState.Landed };
            Assert.False(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                empty, null, out reason, out _));
            string rawReason;
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(empty, out rawReason));
            Assert.Equal(rawReason, reason);
        }

        [Fact]
        public void Validate_EvaHop_ReadsTheTerminalOnTheWalkedTip()
        {
            Install(ReFlySessionShape.EvaReboard, TerminalState.Orbiting, out var fork);

            string rawReason;
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(fork, out rawReason));
            Assert.Equal("null TerminalState", rawReason);

            Assert.True(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                fork, null, out string reason, out SlotVesselWalk walk));
            Assert.Null(reason);
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterBoardId, walk.Tip.RecordingId);
            Assert.Equal(1, walk.EvaHops);
            Assert.Equal(1, walk.BoardHops);
        }

        [Fact]
        public void Validate_EvaHop_UnflownForkWithNoPayloadAnywhere_IsStillRefused()
        {
            // The placeholder guard survives the walk: payload must exist on the fork or
            // on a vessel segment the walk stood on.
            Install(ReFlySessionShape.EvaReboard, TerminalState.Landed, out var fork);
            fork.Points.Clear();
            Rec(ReFlyThroughEvaFixture.ForkAfterEvaId).Points.Clear();
            Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).Points.Clear();

            Assert.False(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                fork, null, out string reason, out _));
            Assert.Equal("empty Points", reason);
        }

        [Fact]
        public void Validate_EvaHop_EmptyForkButFlownContinuation_Validates()
        {
            // The player stepped out at the rewind instant: the fork holds no sample,
            // the flight lives on the continuation.
            Install(ReFlySessionShape.EvaReboard, TerminalState.Landed, out var fork);
            fork.Points.Clear();

            Assert.True(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                fork, null, out string reason, out _));
            Assert.Null(reason);
        }

        [Fact]
        public void Validate_EvaHop_TipWithoutTerminal_IsRefusedNullTerminal()
        {
            Install(ReFlySessionShape.EvaReboard, TerminalState.Landed, out var fork);
            Rec(ReFlyThroughEvaFixture.ForkAfterBoardId).TerminalStateValue = null;

            Assert.False(SupersedeCommit.ValidateReFlySessionSupersedeSource(
                fork, null, out string reason, out SlotVesselWalk walk));
            Assert.Equal("null TerminalState", reason);
            Assert.Equal(ReFlyThroughEvaFixture.ForkAfterBoardId, walk.Tip.RecordingId);
        }

        [Fact]
        public void ConcludeRetiredProvisional_UsesTheSamePredicate_AsAppendRelations()
        {
            // A retired (pruned) provisional has left its tree, so the walk cannot hop
            // and the no-op conclusion route still refuses a validating one.
            Install(ReFlySessionShape.NoBranch, TerminalState.Landed, out var fork);
            Assert.False(SupersedeCommit.ConcludeRetiredProvisional(
                ParsekScenario.Instance.ActiveReFlySessionMarker, fork, "test"));
            Assert.Contains(logLines, l => l.Contains("ConcludeRetiredProvisional: refusing the no-op route"));
        }
    }
}
