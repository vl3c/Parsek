using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the #15 classifier: a Re-Fly puts a vessel the player had already
    /// RECOVERED back in the world while its recovery funds/science/crew rows stay banked.
    /// The classifier decides which ledger actions the resurrection retires.
    ///
    /// <para>
    /// The cell that matters most is the identity gate. Retiring these rows takes funds out
    /// of the player's account, so a false positive is an economy bug. KSP bakes
    /// <c>persistentId</c> into the .craft and reuses it on every launch, so this classifier
    /// requires a POSITIVE launch-guid match and refuses to match on pid alone — deliberately
    /// stricter than <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/>, whose
    /// unknown-guid pid-only fallback is right for visibility decisions and wrong here.
    /// </para>
    /// </summary>
    public class ResurrectionRetirementEligibilityTests
    {
        private const string GuidA = "11111111111111111111111111111111";
        private const string GuidB = "22222222222222222222222222222222";

        private static Recording RecoveredRecording(
            string id, uint pid, string launchGuid, double startUt, double endUt)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = launchGuid,
                ExplicitStartUT = startUt,
                ExplicitEndUT = endUt,
            };
            rec.TerminalStateValue = TerminalState.Recovered;
            return rec;
        }

        private static GameAction RecoveryFunds(string actionId, string recId, double ut, float funds)
        {
            return new GameAction
            {
                ActionId = actionId,
                RecordingId = recId,
                UT = ut,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                FundsAwarded = funds,
            };
        }

        /// <summary>
        /// A production-shaped <see cref="GameActionType.ScienceEarning"/> row.
        ///
        /// <para>
        /// The shape is load-bearing and the earlier fixtures got it wrong.
        /// <c>GameStateEventConverter.ConvertScienceSubjects</c> stamps <c>UT = endUT</c> on
        /// EVERY science row of a recording and stamps <c>Method</c> from the recorder's
        /// reason key, so the ONLY thing distinguishing recovery science from mid-flight
        /// transmitted science on a real row is <c>Method</c> — never the UT. A fixture that
        /// gives science rows distinct per-row UTs describes data production cannot emit,
        /// and a classifier keyed on a UT window passes against it while over-retiring in
        /// the real save.
        /// </para>
        /// </summary>
        private static GameAction Science(
            string actionId, string recId, double endUt, ScienceMethod method)
        {
            return new GameAction
            {
                ActionId = actionId,
                RecordingId = recId,
                UT = endUt,
                Type = GameActionType.ScienceEarning,
                ScienceAwarded = 12f,
                Method = method,
                StartUT = (float)(endUt - 200.0),
                EndUT = (float)endUt,
            };
        }

        /// <summary>Science the recovery awarded — reason key <c>VesselRecovery</c>.</summary>
        private static GameAction RecoveredScience(string actionId, string recId, double endUt)
            => Science(actionId, recId, endUt, ScienceMethod.Recovered);

        /// <summary>Science transmitted mid-flight — reason key <c>ScienceTransmission</c>.</summary>
        private static GameAction TransmittedScience(string actionId, string recId, double endUt)
            => Science(actionId, recId, endUt, ScienceMethod.Transmitted);

        private static GameAction CrewRecovered(string actionId, string recId, double ut)
        {
            return new GameAction
            {
                ActionId = actionId,
                RecordingId = recId,
                UT = ut,
                Type = GameActionType.KerbalAssignment,
                KerbalEndStateField = KerbalEndState.Recovered,
            };
        }

        private static List<(uint pid, string guid)> Survivors(params (uint, string)[] items)
        {
            var list = new List<(uint pid, string guid)>();
            foreach (var item in items) list.Add(item);
            return list;
        }

        // ================================================================
        // Identity gate — the economy-safety property
        // ================================================================

        [Fact]
        public void PidMatchWithMatchingGuid_Classifies()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 500.0, 8000f) };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal("rec-1", result[0].RecordingId);
            Assert.Equal(42u, result[0].LiveVesselPid);
            Assert.Equal(new List<string> { "act-funds" }, result[0].RetiredActionIds);
            Assert.False(result[0].UsedFallbackAnchor);
        }

        [Fact]
        public void GuidMismatch_DoesNotClassify()
        {
            // A relaunch of the SAME craft reuses the baked pid. Matching on it would claw
            // back the funds of a recovery that genuinely happened, for a craft that is a
            // different physical launch. Fails if the guid comparison is dropped.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 500.0, 8000f) };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidB)), new List<Recording> { rec }, actions, 200.0);

            Assert.Empty(result);
        }

        [Fact]
        public void PidOnlyMatchWithUnknownGuidOnEitherSide_DoesNotClassify()
        {
            // THE non-negotiable: this classifier must never fall back to pid-only. Every
            // other identity site in the codebase degrades to pid-only on an unknown guid;
            // here that degradation is a funds bug, so it is refused. Fails if this is
            // rewired onto VesselLaunchIdentity.LiveVesselIsRecordedLaunch.
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 500.0, 8000f) };

            var recUnknown = RecoveredRecording("rec-1", 42u, null, 100.0, 500.0);
            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { recUnknown }, actions, 200.0));

            var recKnown = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, "")), new List<Recording> { recKnown }, actions, 200.0));

            // Sanity: the same inputs DO classify once both guids are known and equal, so the
            // emptiness above is the gate firing rather than the fixture being inert.
            Assert.Single(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { recKnown }, actions, 200.0));
        }

        [Fact]
        public void IsPositivelySameLaunch_RejectsEveryDegenerateInput()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);

            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(null, 42u, GuidA));
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 0u, GuidA));
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 43u, GuidA));
            Assert.True(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 42u, GuidA));

            // Guid normalization: dashed vs undashed must compare equal.
            string dashed = Guid.Parse(GuidA).ToString("D");
            Assert.True(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 42u, dashed));
        }

        // ================================================================
        // Terminal-state and cutoff gates
        // ================================================================

        [Fact]
        public void RecordingNotTerminallyRecovered_DoesNotClassify()
        {
            // A landed-but-not-recovered craft was never paid out; there is nothing to retire.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            rec.TerminalStateValue = TerminalState.Landed;
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 500.0, 8000f) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        [Fact]
        public void RecordingWithNoTerminalVerdict_DoesNotClassify()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            rec.TerminalStateValue = null;
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 500.0, 8000f) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        [Fact]
        public void RecoveryBeforeTheCutoff_IsLeftAlone()
        {
            // A recovery from BEFORE the rewind point is still true in the reverted world —
            // that craft was recovered and stays recovered. Fails if the cutoff comparison
            // is dropped, which would claw back rewards the rewind never undid.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 10.0, 100.0);
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 100.0, 8000f) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        [Fact]
        public void RecoveryExactlyAtTheCutoff_IsLeftAlone()
        {
            // Strictly-after, matching the Rec-1 route-retire convention.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 10.0, 200.0);
            var actions = new List<GameAction> { RecoveryFunds("act-funds", "rec-1", 200.0, 8000f) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        // ================================================================
        // Bundle composition
        // ================================================================

        [Fact]
        public void RecoveryScience_IsBundled()
        {
            // Production shape: BOTH rows carry UT = the recording's endUT, because that is
            // what ConvertScienceSubjects stamps. Only Method tells them apart.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                RecoveredScience("act-sci-recovered-a", "rec-1", 500.0),
                RecoveredScience("act-sci-recovered-b", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal(3, result[0].RetiredActionIds.Count);
            Assert.Contains("act-sci-recovered-a", result[0].RetiredActionIds);
            Assert.Contains("act-sci-recovered-b", result[0].RetiredActionIds);
        }

        [Fact]
        public void ScienceTransmittedDuringTheFlight_IsNotBundled()
        {
            // Science TRANSMITTED mid-mission is not recovery science: the player has it
            // whether or not the craft was ever recovered, and retiring it would take away
            // research the rewind did not undo.
            //
            // The fixture is deliberately the WORST case for a UT-keyed classifier and the
            // ONLY case production emits: the transmitted row sits at exactly the same UT as
            // the recovery anchor, because ConvertScienceSubjects stamps endUT on both. Any
            // |UT delta| window at all bundles this row. Method does not.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                TransmittedScience("act-sci-transmitted", "rec-1", 500.0),
                RecoveredScience("act-sci-recovered", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal(
                new List<string> { "act-funds", "act-sci-recovered" },
                result[0].RetiredActionIds);
        }

        [Fact]
        public void PreRewindTransmittedScience_IsNotRetired_EvenAtTheAnchorUt()
        {
            // Directional cell (a): science TRANSMITTED before the rewind point is already
            // banked in the RP quicksave's R&D and the player still owns it after the
            // resurrection. It carries UT = endUT like every other science row, so it lands
            // right on top of the recovery-funds anchor. Retiring it makes the Step-5
            // authoritative recalc subtract research the rewind never undid.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                TransmittedScience("act-sci-pre-rewind-transmit", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.DoesNotContain("act-sci-pre-rewind-transmit", result[0].RetiredActionIds);
            Assert.Equal(new List<string> { "act-funds" }, result[0].RetiredActionIds);
        }

        [Fact]
        public void RecoveryScience_IsRetired_EvenWhenTheFundsAnchorIsFarFromEndUt()
        {
            // Directional cell (b): the funds anchor's UT is sampled from the live
            // FundsChanged event and need not coincide with the recording's endUT, while
            // every science row is stamped at endUT. A tight |UT delta| window UNDER-retires
            // here — the recovery science stays banked and the double-count survives.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 620.0, 8000f),   // 120 s from endUT
                RecoveredScience("act-sci-recovered", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Contains("act-sci-recovered", result[0].RetiredActionIds);
        }

        [Fact]
        public void CrewRecoveredRows_AreBundledByEndStateNotByUt()
        {
            // The crew's disposition IS the recovery, whenever the row happens to be
            // stamped, so this one is matched on end state rather than on the UT window.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                CrewRecovered("act-crew", "rec-1", 120.0),   // far outside the science window
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Contains("act-crew", result[0].RetiredActionIds);
        }

        [Fact]
        public void CrewRowsWithOtherEndStates_AreNotBundled()
        {
            // A kerbal who DIED on this flight is not part of the recovery bundle; that
            // retirement belongs to the supersede path, not to a resurrection.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var dead = CrewRecovered("act-crew-dead", "rec-1", 400.0);
            dead.KerbalEndStateField = KerbalEndState.Dead;
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                dead,
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.DoesNotContain("act-crew-dead", result[0].RetiredActionIds);
        }

        [Fact]
        public void ActionsFromOtherRecordings_AreNeverBundled()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                RecoveredScience("act-sci-other-rec", "rec-OTHER", 500.0),
                CrewRecovered("act-crew-other-rec", "rec-OTHER", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Equal(new List<string> { "act-funds" }, result[0].RetiredActionIds);
        }

        [Fact]
        public void RecoveryExperienceRows_AreBundledByRecordingMembership()
        {
            // A KerbalExperience row exists ONLY because a recovery archived the crew's flight
            // log, and a recording has at most one recovery — so recording membership alone is
            // the right match, no UT window. Leaving it behind would let the monotone roster
            // re-assert put the recovery flight's XP back onto crews who are flying again in a
            // world where that recovery never happened. Fails if the XP bundle is dropped.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var xp = new GameAction
            {
                ActionId = "act-xp",
                RecordingId = "rec-1",
                UT = 500.0,
                Type = GameActionType.KerbalExperience,
                KerbalName = "Jeb",
                KerbalCareerEntries = "0,Orbit,Kerbin",
            };
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                xp,
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Contains("act-xp", result[0].RetiredActionIds);
        }

        [Fact]
        public void ExperienceRowsFromOtherRecordings_AreNotBundled()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var other = new GameAction
            {
                ActionId = "act-xp-other",
                RecordingId = "rec-OTHER",
                UT = 500.0,
                Type = GameActionType.KerbalExperience,
                KerbalName = "Bill",
                KerbalCareerEntries = "0,Orbit,Kerbin",
            };
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                other,
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Equal(new List<string> { "act-funds" }, result[0].RetiredActionIds);
        }

        [Fact]
        public void ScienceBundleIsKeyedOnMethodAndIgnoresTheUtEntirely()
        {
            // Pins the KEY, not a tolerance. Every row below sits at the same UT (production
            // stamps endUT on all of them), so a classifier that consults the UT at all can
            // only answer "all four" or "none" — both wrong. Fails the moment someone
            // re-introduces a |UT delta| window as the discriminator.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                TransmittedScience("act-sci-transmit-1", "rec-1", 500.0),
                RecoveredScience("act-sci-recovered-1", "rec-1", 500.0),
                TransmittedScience("act-sci-transmit-2", "rec-1", 500.0),
                RecoveredScience("act-sci-recovered-2", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal(
                new List<string> { "act-funds", "act-sci-recovered-1", "act-sci-recovered-2" },
                result[0].RetiredActionIds);
        }

        // ================================================================
        // Zero-value recovery fallback anchor
        // ================================================================

        [Fact]
        public void ZeroValueRecovery_AnchorsOnEndUtAndStillRetiresTheCrewRow()
        {
            // A pod recovered at the pad refunds nothing, so no FundsEarning row exists to
            // anchor on. It still resurrects, and its crew row still needs retiring. Fails if
            // the fallback anchor is dropped — the crew reservation would survive the
            // resurrection.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                CrewRecovered("act-crew", "rec-1", 500.0),
                RecoveredScience("act-sci", "rec-1", 500.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.True(result[0].UsedFallbackAnchor);
            Assert.Equal(500.0, result[0].AnchorUT);
            Assert.Contains("act-crew", result[0].RetiredActionIds);
            Assert.Contains("act-sci", result[0].RetiredActionIds);
        }

        [Fact]
        public void ZeroValueRecoveryEndingBeforeTheCutoff_IsLeftAlone()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 10.0, 100.0);
            var actions = new List<GameAction> { CrewRecovered("act-crew", "rec-1", 100.0) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        [Fact]
        public void RecoveredRecordingWithNothingToRetire_ProducesNoEntry()
        {
            // No funds, no science, no crew rows: there is nothing to tombstone, so the
            // classifier must not emit an empty entry the caller would log as a retirement.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction> { RecoveredScience("act-sci-unrelated", "rec-OTHER", 500.0) };

            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0));
        }

        // ================================================================
        // Multiple resurrections / degenerate inputs
        // ================================================================

        [Fact]
        public void SeveralResurrectedCraft_AreEachClassifiedSeparately()
        {
            var recA = RecoveredRecording("rec-A", 42u, GuidA, 100.0, 500.0);
            var recB = RecoveredRecording("rec-B", 77u, GuidB, 100.0, 600.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-a", "rec-A", 500.0, 8000f),
                RecoveryFunds("act-b", "rec-B", 600.0, 4000f),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA), (77u, GuidB)),
                new List<Recording> { recA, recB }, actions, 200.0);

            Assert.Equal(2, result.Count);
            Assert.Equal(new List<string> { "act-a" }, result[0].RetiredActionIds);
            Assert.Equal(new List<string> { "act-b" }, result[1].RetiredActionIds);
        }

        [Fact]
        public void OnlyTheResurrectedCraftIsClassified_NotItsRecoveredNeighbours()
        {
            // A second recovered craft that did NOT come back (no live survivor) keeps its
            // rewards. Fails if the classifier walks recordings without requiring a survivor.
            var resurrected = RecoveredRecording("rec-back", 42u, GuidA, 100.0, 500.0);
            var stayedRecovered = RecoveredRecording("rec-gone", 77u, GuidB, 100.0, 600.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-back", "rec-back", 500.0, 8000f),
                RecoveryFunds("act-gone", "rec-gone", 600.0, 4000f),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)),
                new List<Recording> { resurrected, stayedRecovered }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal("rec-back", result[0].RecordingId);
        }

        [Fact]
        public void EmptyOrNullInputs_ReturnEmptyWithoutThrowing()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction> { RecoveryFunds("a", "rec-1", 500.0, 1f) };
            var survivors = Survivors((42u, GuidA));

            Assert.Empty(ResurrectionRetirementEligibility.Classify(null, new List<Recording> { rec }, actions, 0.0));
            Assert.Empty(ResurrectionRetirementEligibility.Classify(survivors, null, actions, 0.0));
            Assert.Empty(ResurrectionRetirementEligibility.Classify(survivors, new List<Recording> { rec }, null, 0.0));
            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                survivors, new List<Recording> { rec }, actions, double.NaN));
            Assert.Empty(ResurrectionRetirementEligibility.Classify(
                new List<(uint, string)>(), new List<Recording> { rec }, actions, 0.0));
        }

        [Fact]
        public void RepeatedClassification_IsStable()
        {
            // The caller dedupes tombstone writes against the existing set, but the
            // classifier itself must also be deterministic: a second pass over the same
            // inputs produces the same set, so a re-entered post-load cannot drift.
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 500.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds", "rec-1", 500.0, 8000f),
                RecoveredScience("act-sci", "rec-1", 500.0),
                CrewRecovered("act-crew", "rec-1", 500.0),
            };

            var first = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);
            var second = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Equal(first[0].RetiredActionIds, second[0].RetiredActionIds);
        }

        [Fact]
        public void MultipleRecoveryFundsRowsOnOneRecording_AllAnchorAndAllRetire()
        {
            var rec = RecoveredRecording("rec-1", 42u, GuidA, 100.0, 900.0);
            var actions = new List<GameAction>
            {
                RecoveryFunds("act-funds-1", "rec-1", 500.0, 8000f),
                RecoveryFunds("act-funds-2", "rec-1", 900.0, 200f),
                RecoveredScience("act-sci-at-end", "rec-1", 900.0),
            };

            var result = ResurrectionRetirementEligibility.Classify(
                Survivors((42u, GuidA)), new List<Recording> { rec }, actions, 200.0);

            Assert.Single(result);
            Assert.Equal(500.0, result[0].AnchorUT); // earliest anchor reported
            Assert.Equal(3, result[0].RetiredActionIds.Count);
        }
    }
}
