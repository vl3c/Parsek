using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards the two confidently-wrong recorded signals found by the 2026-08-09 part-action
    /// recording audit — signals playback rendered faithfully, which is what made them bugs rather
    /// than fidelity gaps:
    ///
    ///   1. A parachute REPACK was recorded as a CUT, because the recorder collapsed STOWED, ACTIVE
    ///      and CUT into one state. Playback's cut handler hides the cap and nothing ever restored
    ///      it, so a repacked chute rendered as an empty can for the rest of the recording.
    ///   2. WHEEL SPIN recorded ModuleWheelMotor.driveOutput — an unsigned percent of max torque —
    ///      and replayed it at `value * 6` deg/s as if it were RPM. A coasting rover showed
    ///      stationary wheels, reverse was identical to forward, and the magnitude was meaningless.
    ///      The recorded signal is gone; the spin is derived from ground speed at playback.
    /// </summary>
    [Collection("Sequential")]
    public class RecordedSignalFixTests : IDisposable
    {
        private readonly string tempDir;

        public RecordedSignalFixTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-recorded-signal-fix-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // Fix 1a — the four-state parachute classifier
        // ------------------------------------------------------------------

        #region ClassifyParachuteState — KSP's five states onto Parsek's four

        [Fact]
        public void ClassifyParachuteState_StowedAndActive_BothMapToStowed()
        {
            // ACTIVE is armed-but-undeployed: canopy hidden, cap on, visually identical to STOWED.
            Assert.Equal(
                FlightRecorder.ParachuteStateStowed,
                FlightRecorder.ClassifyParachuteState(ModuleParachute.deploymentStates.STOWED));
            Assert.Equal(
                FlightRecorder.ParachuteStateStowed,
                FlightRecorder.ClassifyParachuteState(ModuleParachute.deploymentStates.ACTIVE));
        }

        [Fact]
        public void ClassifyParachuteState_SemiDeployedAndDeployed_MapToDistinctStates()
        {
            Assert.Equal(
                FlightRecorder.ParachuteStateSemiDeployed,
                FlightRecorder.ClassifyParachuteState(ModuleParachute.deploymentStates.SEMIDEPLOYED));
            Assert.Equal(
                FlightRecorder.ParachuteStateDeployed,
                FlightRecorder.ClassifyParachuteState(ModuleParachute.deploymentStates.DEPLOYED));
        }

        [Fact]
        public void ClassifyParachuteState_Cut_NoLongerCollapsesIntoStowed()
        {
            // THE regression. Under the old three-state encoding CUT returned 0 (stowed), which made
            // CUT -> STOWED (a repack) indistinguishable from DEPLOYED -> CUT.
            int cut = FlightRecorder.ClassifyParachuteState(ModuleParachute.deploymentStates.CUT);
            Assert.Equal(FlightRecorder.ParachuteStateCut, cut);
            Assert.NotEqual(FlightRecorder.ParachuteStateStowed, cut);
        }

        [Fact]
        public void ClassifyParachuteState_AllFourStatesAreDistinct()
        {
            var states = new HashSet<int>
            {
                FlightRecorder.ParachuteStateStowed,
                FlightRecorder.ParachuteStateSemiDeployed,
                FlightRecorder.ParachuteStateDeployed,
                FlightRecorder.ParachuteStateCut
            };
            Assert.Equal(4, states.Count);
        }

        #endregion

        #region ClassifyParachuteTransitionEvent — the transition table

        [Fact]
        public void Transition_CutToStowed_IsRepacked_NotCut()
        {
            // The headline fix: an EVA repack must not replay as a cut.
            Assert.Equal(
                PartEventType.ParachuteRepacked,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    FlightRecorder.ParachuteStateCut, FlightRecorder.ParachuteStateStowed));
        }

        [Fact]
        public void Transition_DeployedToCut_IsCut()
        {
            Assert.Equal(
                PartEventType.ParachuteCut,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    FlightRecorder.ParachuteStateDeployed, FlightRecorder.ParachuteStateCut));
        }

        [Fact]
        public void Transition_SemiDeployedToCut_IsCut()
        {
            Assert.Equal(
                PartEventType.ParachuteCut,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    FlightRecorder.ParachuteStateSemiDeployed, FlightRecorder.ParachuteStateCut));
        }

        [Theory]
        [InlineData(0)] // Stowed
        [InlineData(1)] // SemiDeployed
        [InlineData(2)] // Deployed
        [InlineData(3)] // Cut
        public void Transition_SameState_EmitsNoEvent(int state)
        {
            Assert.Null(FlightRecorder.ClassifyParachuteTransitionEvent(state, state));
        }

        [Fact]
        public void Transition_StowedToCut_EmitsNoEvent_FirstSightOfAnAlreadyCutChute()
        {
            // Not a physical transition — stock reaches CUT only from SEMIDEPLOYED/DEPLOYED. It is
            // what the FIRST poll of an already-cut chute looks like, because an unseen pid defaults
            // to Stowed. Emitting a cut there would be a spurious event at segment start.
            Assert.Null(FlightRecorder.ClassifyParachuteTransitionEvent(
                FlightRecorder.ParachuteStateStowed, FlightRecorder.ParachuteStateCut));
        }

        [Fact]
        public void Transition_StowedToSemiDeployed_IsSemiDeployed()
        {
            Assert.Equal(
                PartEventType.ParachuteSemiDeployed,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    FlightRecorder.ParachuteStateStowed, FlightRecorder.ParachuteStateSemiDeployed));
        }

        [Theory]
        [InlineData(0)] // from Stowed
        [InlineData(1)] // from SemiDeployed
        [InlineData(3)] // from Cut (stock needs a Repack first, but tolerate it)
        public void Transition_AnythingToDeployed_IsDeployed(int oldState)
        {
            Assert.Equal(
                PartEventType.ParachuteDeployed,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    oldState, FlightRecorder.ParachuteStateDeployed));
        }

        [Fact]
        public void Transition_CutToSemiDeployed_IsSemiDeployed()
        {
            Assert.Equal(
                PartEventType.ParachuteSemiDeployed,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    FlightRecorder.ParachuteStateCut, FlightRecorder.ParachuteStateSemiDeployed));
        }

        [Theory]
        [InlineData(1)] // SemiDeployed -> Stowed
        [InlineData(2)] // Deployed -> Stowed
        public void Transition_CanopyOutToStowed_KeepsThePreSplitCutVerdict(int oldState)
        {
            // Not a stock path, but it was ParachuteCut before the four-state split and there is no
            // reason to change what it reports.
            Assert.Equal(
                PartEventType.ParachuteCut,
                FlightRecorder.ClassifyParachuteTransitionEvent(
                    oldState, FlightRecorder.ParachuteStateStowed));
        }

        #endregion

        #region CheckParachuteTransition — end to end over the shared state map

        [Fact]
        public void CheckParachuteTransition_DeployCutRepack_EmitsRepackedNotASecondCut()
        {
            // The exact bug sequence, end to end through the dictionary the recorder actually uses.
            var map = new Dictionary<uint, int>();
            const uint pid = 42u;

            var deployed = FlightRecorder.CheckParachuteTransition(
                pid, "parachuteSingle", FlightRecorder.ParachuteStateDeployed, map, 100.0);
            var cut = FlightRecorder.CheckParachuteTransition(
                pid, "parachuteSingle", FlightRecorder.ParachuteStateCut, map, 110.0);
            var repacked = FlightRecorder.CheckParachuteTransition(
                pid, "parachuteSingle", FlightRecorder.ParachuteStateStowed, map, 200.0);

            Assert.Equal(PartEventType.ParachuteDeployed, deployed.Value.eventType);
            Assert.Equal(PartEventType.ParachuteCut, cut.Value.eventType);
            Assert.Equal(PartEventType.ParachuteRepacked, repacked.Value.eventType);
            Assert.Equal(200.0, repacked.Value.ut);
            Assert.Equal(pid, repacked.Value.partPersistentId);

            // Back to stowed: the entry is dropped, so the map only ever holds non-default chutes.
            Assert.False(map.ContainsKey(pid));
        }

        [Fact]
        public void CheckParachuteTransition_Cut_StoresTheCutStateSoARepackIsDetectable()
        {
            var map = new Dictionary<uint, int>();
            const uint pid = 7u;

            FlightRecorder.CheckParachuteTransition(
                pid, "chute", FlightRecorder.ParachuteStateDeployed, map, 10.0);
            FlightRecorder.CheckParachuteTransition(
                pid, "chute", FlightRecorder.ParachuteStateCut, map, 20.0);

            Assert.Equal(FlightRecorder.ParachuteStateCut, map[pid]);
        }

        [Fact]
        public void CheckParachuteTransition_PolledTwiceAtTheSameState_EmitsNothing()
        {
            var map = new Dictionary<uint, int>();
            const uint pid = 9u;

            FlightRecorder.CheckParachuteTransition(
                pid, "chute", FlightRecorder.ParachuteStateDeployed, map, 10.0);
            var second = FlightRecorder.CheckParachuteTransition(
                pid, "chute", FlightRecorder.ParachuteStateDeployed, map, 11.0);

            Assert.Null(second);
        }

        [Fact]
        public void CheckParachuteTransition_PrimedAsCut_ThenRepacked_EmitsRepacked()
        {
            // What PartStateSeeder.SeedParachutes now sets up: a recording that STARTS with a cut
            // chute. Priming the shared map as Cut is what makes the later repack observable at all.
            var map = new Dictionary<uint, int> { [5u] = FlightRecorder.ParachuteStateCut };

            var evt = FlightRecorder.CheckParachuteTransition(
                5u, "chute", FlightRecorder.ParachuteStateStowed, map, 50.0);

            Assert.Equal(PartEventType.ParachuteRepacked, evt.Value.eventType);
        }

        [Fact]
        public void CheckParachuteTransition_UnprimedAlreadyCutChute_DoesNotEmitASpuriousCut()
        {
            var map = new Dictionary<uint, int>();

            var evt = FlightRecorder.CheckParachuteTransition(
                5u, "chute", FlightRecorder.ParachuteStateCut, map, 50.0);

            Assert.Null(evt);
            // The state is still recorded, so the repack that follows is detectable.
            Assert.Equal(FlightRecorder.ParachuteStateCut, map[5u]);
        }

        #endregion

        #region ClassifyPartDeath — the state-3 trap

        [Fact]
        public void ClassifyPartDeath_CutChuteDies_IsOrdinaryDestroyed_NotParachuteDestroyed()
        {
            // Guards the `state > 0` trap: Cut is 3, so a truthy test would have called this an
            // aero-destroyed canopy. Before the split a cut chute was erased from the map, which is
            // why `state > 0` used to be right.
            var map = new Dictionary<uint, int> { [1u] = FlightRecorder.ParachuteStateCut };

            Assert.Equal(
                PartEventType.Destroyed,
                FlightRecorder.ClassifyPartDeath(1u, hasParachuteModule: true, parachuteStates: map));
        }

        [Theory]
        [InlineData(1)] // SemiDeployed
        [InlineData(2)] // Deployed
        public void ClassifyPartDeath_CanopyOutWhenItDies_IsParachuteDestroyed(int state)
        {
            var map = new Dictionary<uint, int> { [1u] = state };

            Assert.Equal(
                PartEventType.ParachuteDestroyed,
                FlightRecorder.ClassifyPartDeath(1u, hasParachuteModule: true, parachuteStates: map));
            // The deployed-chute entry is consumed on death.
            Assert.False(map.ContainsKey(1u));
        }

        [Fact]
        public void IsDeployedParachuteState_OnlyCanopyOutStatesCount()
        {
            Assert.False(FlightRecorder.IsDeployedParachuteState(FlightRecorder.ParachuteStateStowed));
            Assert.True(FlightRecorder.IsDeployedParachuteState(FlightRecorder.ParachuteStateSemiDeployed));
            Assert.True(FlightRecorder.IsDeployedParachuteState(FlightRecorder.ParachuteStateDeployed));
            Assert.False(FlightRecorder.IsDeployedParachuteState(FlightRecorder.ParachuteStateCut));
        }

        #endregion

        // ------------------------------------------------------------------
        // Fix 1b — serialization stability and split-seed family placement
        // ------------------------------------------------------------------

        #region Enum stability

        [Fact]
        public void ParachuteRepacked_IsExplicitlyNumbered35_AndAcceptedByTheCodecGate()
        {
            Assert.Equal(35, (int)PartEventType.ParachuteRepacked);
            // TrajectoryTextSidecarCodec admits a part-event type through
            // Enum.IsDefined(typeof(PartEventType), typeInt), so a defined 35 is an accepted 35.
            Assert.True(Enum.IsDefined(typeof(PartEventType), 35));
            // The values it must not have disturbed.
            Assert.Equal(3, (int)PartEventType.ParachuteCut);
            Assert.Equal(34, (int)PartEventType.ThermalAnimationMedium);
        }

        // WHY NO SCHEMA-GENERATION BUMP — the real mechanism, corrected 2026-08-11.
        //
        // Adding member 35 did not bump RecordingStore.CurrentRecordingSchemaGeneration, and the
        // reason is UNDEFINED-ENUM TOLERANCE ACROSS EVERY CONSUMER, not "an older build skips the
        // unknown event". Skipping is what the LEGACY TEXT reader does
        // (TrajectoryTextSidecarCodec.DeserializePartEvents gates on
        // Enum.IsDefined and `continue`s). The SHIPPING BINARY reader does not gate at all:
        // TrajectorySidecarBinary.ReadPartEventList does a bare
        // `eventType = (PartEventType)reader.ReadInt32()`, so an older build reading a newer .prec
        // MATERIALISES an undefined (PartEventType)35 into its PartEvents list and carries it.
        //
        // That is still safe, because every reachable consumer of an unrecognised member degrades
        // gracefully, and the cell below pins each one:
        //   ApplyPartEvents          - switch with no matching case, no default: silently unhandled.
        //   IsPermanentVisualStateEvent / IsInertPartEventForTailTrim  -> false.
        //   SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent          -> true  (keeps the segment).
        //   GhostingTriggerClassifier.IsGhostingTrigger                -> true  + a Verbose
        //                                                                 "unknown PartEventType".
        //   Re-serialisation preserves the raw int (the writer casts back to int).
        // So the outcome the commit claimed holds — the bump is correctly avoided — but the stated
        // mechanism did not. This comment and the todo entry are the correction of record; the
        // commit message is immutable and overstates it.
        [Fact]
        public void BinarySidecar_RawCastsAnUndefinedPartEventType_AndEveryConsumerDegradesGracefully()
        {
            // A value no PartEventType member has, standing in for "member added by a future build".
            const int futureTypeInt = 9977;
            var futureType = (PartEventType)futureTypeInt;
            Assert.False(Enum.IsDefined(typeof(PartEventType), futureTypeInt));

            var original = new Recording
            {
                RecordingId = "undefined-part-event-type",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration
            };
            original.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.1,
                longitude = -74.5,
                altitude = 120,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 50f, 0f),
                bodyName = "Kerbin"
            });
            original.Points.Add(new TrajectoryPoint
            {
                ut = 110,
                latitude = -0.09,
                longitude = -74.49,
                altitude = 300,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 75f, 0f),
                bodyName = "Kerbin"
            });
            original.PartEvents.Add(new PartEvent
            {
                ut = 105.0,
                partPersistentId = 4242u,
                eventType = futureType,
                partName = "parachuteSingle",
                value = 0f,
                moduleIndex = 0
            });

            string path = Path.Combine(tempDir, "undefined-part-event-type.prec");
            TrajectorySidecarBinary.Write(path, original, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.True(probe.Supported);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            // (1) The reader raw-casts: the undefined int SURVIVES verbatim. It is neither skipped
            //     (the text reader's behaviour) nor coerced to a defined member.
            Assert.Single(restored.PartEvents);
            PartEvent roundTripped = restored.PartEvents[0];
            Assert.Equal(futureTypeInt, (int)roundTripped.eventType);
            Assert.Equal(4242u, roundTripped.partPersistentId);
            Assert.Equal(105.0, roundTripped.ut);
            Assert.False(Enum.IsDefined(typeof(PartEventType), (int)roundTripped.eventType));

            // (2) Re-serialising the materialised event preserves the int rather than dropping it,
            //     so a round trip through an older build does not silently erase the newer signal.
            string rewritePath = Path.Combine(tempDir, "undefined-part-event-type-rewrite.prec");
            TrajectorySidecarBinary.Write(rewritePath, restored, sidecarEpoch: 2);
            Assert.True(TrajectorySidecarBinary.TryProbe(rewritePath, out TrajectorySidecarProbe reprobe));
            var reread = new Recording();
            TrajectorySidecarBinary.Read(rewritePath, reread, reprobe);
            Assert.Single(reread.PartEvents);
            Assert.Equal(futureTypeInt, (int)reread.PartEvents[0].eventType);

            // (3) Every consumer reachable headless takes its safe branch.
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(roundTripped.eventType));
            Assert.False(RecordingOptimizer.IsInertPartEventForTailTrim(roundTripped));
            Assert.True(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(roundTripped));
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(roundTripped.eventType));
        }

        #endregion

        #region Split-seed family placement

        [Fact]
        public void IsPermanentVisualStateEvent_RepackAndCutAreBothReversible()
        {
            // A repack undoes a cut, so neither can be permanent. Only the DESTROYED chute is:
            // that part is gone and the tail has to keep it hidden.
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ParachuteRepacked));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ParachuteCut));
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ParachuteDestroyed));
        }

        [Fact]
        public void ForwardPermanentStateEvents_CutThenRepack_ForwardsNoParachuteSeed()
        {
            var firstHalf = new List<PartEvent>
            {
                Chute(100.0, PartEventType.ParachuteDeployed),
                Chute(110.0, PartEventType.ParachuteCut),
                Chute(200.0, PartEventType.ParachuteRepacked)
            };
            var secondHalf = new List<PartEvent>();

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, 300.0);

            Assert.Empty(secondHalf);
        }

        [Fact]
        public void BuildTransientStateSeeds_TailAfterCutThenRepack_SeedsTheRepackedPose()
        {
            // The brief's split-tail case, RESTATED for the bidirectional seed emitter this
            // fix was merged with. The visual verdict is unchanged — the tail renders
            // canopy hidden, cap on, which is precisely a repacked chute — but the route to
            // it is now explicit rather than implied.
            //
            // WHAT CHANGED AND WHY. This cell used to assert the tail carried NO parachute
            // seed: a repack collapsed to "inactive" exactly like a cut, the active-only
            // emitter emitted nothing for an inactive state, and the tail fell back to the
            // ghost's BUILD-TIME pose, which happened to be the stowed-with-cap pose. That
            // reasoning depended on the tail's ghost spawning at the all-stowed prefab pose.
            // It no longer does: a split TIP spawns from a snapshot baseline, and a TIP's
            // snapshot is a COPY of the parent's LAUNCH-time snapshot, so "no seed" now
            // means "whatever the chute looked like at launch" — which for a chute that
            // deployed and was repacked mid-flight is not the repacked pose at all. The
            // terminal state has to be STATED, so AppendReversibleStateSeeds emits the
            // inactive direction verbatim.
            //
            // The original risk this cell was written against is still guarded, and by the
            // same assertion: the seed must be ParachuteRepacked and NOT ParachuteCut. A cut
            // seed would hide the cap again, which was the whole defect.
            var events = new List<PartEvent>
            {
                Chute(100.0, PartEventType.ParachuteDeployed),
                Chute(110.0, PartEventType.ParachuteCut),
                Chute(200.0, PartEventType.ParachuteRepacked)
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);

            var chuteSeed = Assert.Single(seeds.Where(s => IsParachuteEvent(s.eventType)));
            Assert.Equal(PartEventType.ParachuteRepacked, chuteSeed.eventType);
            Assert.Equal(300.0, chuteSeed.ut);

            // And the seeded type is the one that puts the cap back — the rendered pose the
            // cell's old name asserted, now read off the seed instead of off the prefab.
            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(
                chuteSeed.eventType, out bool capActive));
            Assert.True(capActive);
        }

        [Fact]
        public void BuildTransientStateSeeds_TailAfterDeployOnly_DoesSeedTheDeploy()
        {
            // Control for the test above: proves the parachute seed machinery is live, so the
            // "no seed" result there is a real verdict rather than a silently dead code path.
            var events = new List<PartEvent> { Chute(100.0, PartEventType.ParachuteDeployed) };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);

            Assert.Contains(seeds, s => s.eventType == PartEventType.ParachuteDeployed);
        }

        [Fact]
        public void BuildTransientStateSeeds_RedeployAfterRepack_SeedsTheDeploy()
        {
            // Last-wins still works with the new member in the family: a chute that is out again at
            // the split must be seeded out.
            var events = new List<PartEvent>
            {
                Chute(100.0, PartEventType.ParachuteDeployed),
                Chute(110.0, PartEventType.ParachuteCut),
                Chute(200.0, PartEventType.ParachuteRepacked),
                Chute(250.0, PartEventType.ParachuteDeployed)
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);

            Assert.Contains(seeds, s => s.eventType == PartEventType.ParachuteDeployed);
        }

        private static PartEvent Chute(double ut, PartEventType type)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = 4242u,
                eventType = type,
                partName = "parachuteSingle"
            };
        }

        private static bool IsParachuteEvent(PartEventType type)
        {
            return type == PartEventType.ParachuteSemiDeployed
                || type == PartEventType.ParachuteDeployed
                || type == PartEventType.ParachuteCut
                || type == PartEventType.ParachuteDestroyed
                || type == PartEventType.ParachuteRepacked;
        }

        #endregion

        // ------------------------------------------------------------------
        // Fix 1c — playback cap bookkeeping
        // ------------------------------------------------------------------

        #region Cap restore

        [Fact]
        public void TryResolveParachuteCapActive_RepackIsTheOnlyEventThatRestoresTheCap()
        {
            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(
                PartEventType.ParachuteRepacked, out bool repackCap));
            Assert.True(repackCap);

            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(
                PartEventType.ParachuteCut, out bool cutCap));
            Assert.False(cutCap);
        }

        [Theory]
        [InlineData(PartEventType.ParachuteSemiDeployed)]
        [InlineData(PartEventType.ParachuteDeployed)]
        [InlineData(PartEventType.ParachuteDestroyed)]
        public void TryResolveParachuteCapActive_EveryOtherParachuteEvent_LeavesTheCapOff(
            PartEventType type)
        {
            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(type, out bool capActive));
            Assert.False(capActive);
        }

        [Theory]
        [InlineData(PartEventType.EngineIgnited)]
        [InlineData(PartEventType.GearDeployed)]
        [InlineData(PartEventType.Decoupled)]
        public void TryResolveParachuteCapActive_NonParachuteEvent_SaysNothingAboutACap(
            PartEventType type)
        {
            Assert.False(GhostPlaybackLogic.TryResolveParachuteCapActive(type, out _));
        }

        [Fact]
        public void ShouldPrewarmHiddenGhostForPartEvent_Repacked_BuildsTheGhostSoTheCapRestoreLands()
        {
            Assert.True(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(
                PartEventType.ParachuteRepacked));
        }

        [Fact]
        public void IsGhostingTrigger_Repacked_IsHandledExplicitly_NotByTheUnknownTypeFallback()
        {
            // The fallback returns true as well, so a bare `Assert.True` would pass even if the
            // member were unhandled. The log line is what discriminates: an explicitly handled type
            // must not emit the "unknown PartEventType" diagnostic.
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            bool isTrigger = GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ParachuteRepacked);

            Assert.True(isTrigger);
            Assert.DoesNotContain(logLines, l => l.Contains("unknown PartEventType"));

            // Sanity: an undefined value DOES take the fallback and log, proving the sink is wired
            // and the assertion above is discriminating.
            GhostingTriggerClassifier.IsGhostingTrigger((PartEventType)9999);
            Assert.Contains(logLines,
                l => l.Contains("[ChainWalker]") && l.Contains("unknown PartEventType"));
        }

        #endregion

        // ------------------------------------------------------------------
        // Fix 2 — wheel spin derived from ground speed
        // ------------------------------------------------------------------

        #region Recorder gate

        [Fact]
        public void IsWheelMotorSpinModuleName_OnlyTheMotorModules()
        {
            Assert.True(FlightRecorder.IsWheelMotorSpinModuleName("ModuleWheelMotor"));
            Assert.True(FlightRecorder.IsWheelMotorSpinModuleName("ModuleWheelMotorSteering"));

            // This one is the VISUAL-MODE test, so steering must stay out of it: a caliper is a
            // WheelSteeringHeading visual, not a continuously spinning wheel.
            Assert.False(FlightRecorder.IsWheelMotorSpinModuleName("ModuleWheelSuspension"));
            Assert.False(FlightRecorder.IsWheelMotorSpinModuleName("ModuleWheelSteering"));
            Assert.False(FlightRecorder.IsWheelMotorSpinModuleName("ModuleRoboticServoRotor"));
            Assert.False(FlightRecorder.IsWheelMotorSpinModuleName("ModuleEngines"));
        }

        [Fact]
        public void IsDerivedWheelVisualModuleName_CoversSteeringToo_ButNotSuspension()
        {
            // The recorder emission gate is wider than the visual-mode test: playback discards every
            // RoboticMotion* event for BOTH derived wheel modes, so emitting either scalar is a
            // write-only surface. Suspension is still a genuine recorded signal.
            Assert.True(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleWheelMotor"));
            Assert.True(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleWheelMotorSteering"));
            Assert.True(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleWheelSteering"));

            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleWheelSuspension"));
            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleRoboticServoRotor"));
            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleRoboticServoHinge"));
            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleEngines"));
            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName(null));
        }

        [Fact]
        public void IsDerivedWheelVisualModuleName_IsASupersetOfTheMotorSpinTest()
        {
            // Structural: the emission gate must never be NARROWER than the visual-mode test, or a
            // module would be recorded and then ignored at playback — the exact write-only surface
            // this gate exists to delete.
            string[] names =
            {
                "ModuleWheelMotor", "ModuleWheelMotorSteering", "ModuleWheelSteering",
                "ModuleWheelSuspension", "ModuleRoboticServoHinge", "ModuleRoboticServoPiston",
                "ModuleRoboticRotationServo", "ModuleRoboticServoRotor", "ModuleEngines"
            };
            foreach (string name in names)
            {
                if (FlightRecorder.IsWheelMotorSpinModuleName(name))
                    Assert.True(FlightRecorder.IsDerivedWheelVisualModuleName(name), name);
            }
        }

        [Fact]
        public void IsRoboticModuleName_StillMatchesWheelMotors_SoModuleIndicesStayInLockstep()
        {
            // The emission gate must NOT be folded into IsRoboticModuleName: that predicate assigns
            // the sequential roboticModuleIndex in both CacheRoboticModules and the ghost builder's
            // TryBuildRoboticInfos, and the playback key is EncodeEngineKey(pid, moduleIndex).
            // Skipping wheel motors there would renumber every later robotic module on the part.
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelMotor"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelMotorSteering"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelSuspension"));
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelSteering"));
        }

        #endregion

        #region ComputeSurfaceHorizontalVelocity

        [Fact]
        public void ComputeSurfaceHorizontalVelocity_ParkedOnARotatingBody_IsZero()
        {
            // The trap this subtraction exists for. TrajectoryPoint.velocity is recorded as
            // rb_velocityD + Krakensbane.GetFrameVelocity() — an ORBITAL velocity. A rover parked on
            // Kerbin's equator carries ~175 m/s of the planet's own rotation. Feeding that to a wheel
            // would spin a stationary rover's wheels at highway speed.
            var up = new Vector3(0f, 1f, 0f);
            var bodyRotation = new Vector3(175f, 0f, 0f);

            Vector3 horiz = GhostPlaybackLogic.ComputeSurfaceHorizontalVelocity(
                worldVelocity: bodyRotation, bodyFrameVelocity: bodyRotation, up: up);

            Assert.True(horiz.magnitude < 1e-4f, $"expected ~0, got {horiz.magnitude}");
        }

        [Fact]
        public void ComputeSurfaceHorizontalVelocity_DrivingOnARotatingBody_LeavesOnlyGroundSpeed()
        {
            var up = new Vector3(0f, 1f, 0f);
            var bodyRotation = new Vector3(175f, 0f, 0f);
            // Driving 10 m/s along +z on top of the planet's rotation.
            var world = bodyRotation + new Vector3(0f, 0f, 10f);

            Vector3 horiz = GhostPlaybackLogic.ComputeSurfaceHorizontalVelocity(world, bodyRotation, up);

            Assert.Equal(10.0, (double)horiz.magnitude, 3);
            Assert.Equal(10.0, (double)horiz.z, 3);
        }

        [Fact]
        public void ComputeSurfaceHorizontalVelocity_ClimbAndSink_AreRemoved()
        {
            var up = new Vector3(0f, 1f, 0f);
            var world = new Vector3(0f, 50f, 0f); // pure vertical

            Vector3 horiz = GhostPlaybackLogic.ComputeSurfaceHorizontalVelocity(
                world, Vector3.zero, up);

            Assert.True(horiz.magnitude < 1e-4f, $"expected ~0, got {horiz.magnitude}");
        }

        #endregion

        #region ComputeWheelRollForward

        [Fact]
        public void ComputeWheelRollForward_IsPerpendicularToBothAxisAndUp()
        {
            var axis = new Vector3(1f, 0f, 0f); // wheel spins about world +x
            var up = new Vector3(0f, 1f, 0f);

            Vector3 roll = GhostPlaybackLogic.ComputeWheelRollForward(axis, up);

            Assert.Equal(1.0, (double)roll.magnitude, 3);
            Assert.Equal(0.0, (double)Vector3.Dot(roll, axis), 3);
            Assert.Equal(0.0, (double)Vector3.Dot(roll, up), 3);
        }

        [Fact]
        public void ComputeWheelRollForward_MirroredWheels_RollOppositeWaysForTheSameLocalSpin()
        {
            // Wheels on opposite sides of a rover have opposite local spin axes. Both must
            // counter-rotate to travel the same way, which is exactly what the cross product yields
            // — this is why the sign comes from the wheel's own axis rather than a guessed vessel
            // forward direction.
            var up = new Vector3(0f, 1f, 0f);

            Vector3 left = GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(1f, 0f, 0f), up);
            Vector3 right = GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(-1f, 0f, 0f), up);

            Assert.Equal(-1.0, (double)Vector3.Dot(left, right), 3);
        }

        [Fact]
        public void ComputeWheelRollForward_DegenerateInputs_ReturnZero()
        {
            var up = new Vector3(0f, 1f, 0f);

            // Axis parallel to up: a wheel lying flat has no rolling direction.
            Assert.Equal(Vector3.zero, GhostPlaybackLogic.ComputeWheelRollForward(up, up));
            Assert.Equal(Vector3.zero,
                GhostPlaybackLogic.ComputeWheelRollForward(Vector3.zero, up));
            Assert.Equal(Vector3.zero,
                GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(1f, 0f, 0f), Vector3.zero));
        }

        #endregion

        #region ComputeWheelSpinDeltaDegrees

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_SpeedOverRadius_InDegrees()
        {
            // 10 m/s on a 0.5 m wheel = 20 rad/s = 1145.9159 deg/s; over 0.5 s that is 572.958 deg.
            var roll = new Vector3(0f, 0f, 1f);
            var velocity = new Vector3(0f, 0f, 10f);

            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                velocity, roll, wheelRadius: 0.5f, deltaSeconds: 0.5);

            Assert.Equal(20.0 * Mathf.Rad2Deg * 0.5, (double)deg, 2);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_SmallerWheelSpinsFaster()
        {
            var roll = new Vector3(0f, 0f, 1f);
            var velocity = new Vector3(0f, 0f, 10f);

            float small = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(velocity, roll, 0.25f, 1.0);
            float large = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(velocity, roll, 1.0f, 1.0);

            Assert.True(small > large, $"small={small} large={large}");
            Assert.Equal(4.0, (double)(small / large), 2);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_Reverse_IsTheNegativeOfForward()
        {
            // The recorded signal could not express this at all: driveOutput is Mathf.Abs(...), so
            // reverse was byte-identical to forward.
            var roll = new Vector3(0f, 0f, 1f);

            float forward = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(0f, 0f, 10f), roll, 0.5f, 1.0);
            float reverse = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(0f, 0f, -10f), roll, 0.5f, 1.0);

            Assert.True(forward > 0f, $"forward should be positive, got {forward}");
            Assert.True(reverse < 0f, $"reverse should be negative, got {reverse}");
            Assert.Equal((double)(-forward), (double)reverse, 3);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_ZeroSpeed_IsStationary()
        {
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                Vector3.zero, new Vector3(0f, 0f, 1f), 0.5f, 1.0);

            Assert.Equal(0f, deg);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_CreepBelowThreshold_IsStationary()
        {
            // A parked ghost must hold still rather than creep on interpolation noise.
            var roll = new Vector3(0f, 0f, 1f);
            float belowThreshold = GhostPlaybackLogic.WheelStationarySpeedMetersPerSecond * 0.5f;

            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(0f, 0f, belowThreshold), roll, 0.5f, 1.0);

            Assert.Equal(0f, deg);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_MotionAcrossTheWheel_DoesNotSpinIt()
        {
            // Sliding sideways is not rolling: velocity orthogonal to the roll direction projects
            // to zero.
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(10f, 0f, 0f), new Vector3(0f, 0f, 1f), 0.5f, 1.0);

            Assert.Equal(0f, deg);
        }

        [Theory]
        [InlineData(0.0)]   // no elapsed time
        [InlineData(-1.0)]  // clock went backwards
        public void ComputeWheelSpinDeltaDegrees_NonPositiveDelta_IsZero(double deltaSeconds)
        {
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 1f), 0.5f, deltaSeconds);

            Assert.Equal(0f, deg);
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_UnusableRadius_IsZero()
        {
            var roll = new Vector3(0f, 0f, 1f);
            var velocity = new Vector3(0f, 0f, 10f);

            Assert.Equal(0f, GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(velocity, roll, 0f, 1.0));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(velocity, roll, -1f, 1.0));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                velocity, roll, GhostPlaybackLogic.MinWheelRadiusMeters * 0.5f, 1.0));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                velocity, roll, float.NaN, 1.0));
        }

        [Fact]
        public void ComputeWheelSpinDeltaDegrees_DegenerateRollDirection_IsZero()
        {
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                new Vector3(0f, 0f, 10f), Vector3.zero, 0.5f, 1.0);

            Assert.Equal(0f, deg);
        }

        [Fact]
        public void DefaultWheelRadius_IsAUsablePositiveFallback()
        {
            Assert.True(GhostPlaybackLogic.DefaultWheelRadiusMeters
                > GhostPlaybackLogic.MinWheelRadiusMeters);
        }

        #endregion

        #region Ground-contact gate — the derivation must not spin wheels off the ground

        // Review finding D1. Speed/radius is only a wheel rate while the wheel is ON something. A
        // rover carried to orbit on a launch vehicle keeps the recording's orbital velocity
        // (~2100 m/s on a Kerbin parking orbit), which ungated turns into thousands of degrees per
        // frame — visible strobing for the whole ascent and coast. The OLD recorded signal was right
        // here by accident (driveOutput is 0 with no motor input), so an ungated derivation is a NEW
        // artifact, and it appears on already-recorded flights too.

        [Fact]
        public void IsWheelSpinGroundContactEnvironment_OnlyTheTwoSurfaceClassesAreContact()
        {
            Assert.True(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.SurfaceMobile));
            Assert.True(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.SurfaceStationary));

            Assert.False(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.Atmospheric));
            Assert.False(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.Approach));
            Assert.False(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.ExoPropulsive));
            Assert.False(GhostPlaybackLogic.IsWheelSpinGroundContactEnvironment(
                SegmentEnvironment.ExoBallistic));
        }

        [Fact]
        public void ResolveWheelGroundContact_TheAscentCase_HoldsTheWheelsStillOffTheGround()
        {
            // The reviewer's fixture: a rover riding a launch vehicle. Pad -> ascent -> orbit.
            List<TrackSection> sections = AscentSections();
            var memo = default(WheelGroundContactMemo);

            // On the pad: contact.
            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 105.0, ref memo));
            // Climbing through atmosphere: no contact.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 250.0, ref memo));
            // Powered above the atmosphere: no contact.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 450.0, ref memo));
            // Coasting in orbit at ~2100 m/s — the strobing case: no contact.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 900.0, ref memo));
        }

        [Fact]
        public void ResolveWheelGroundContact_DrivingOnTheSurface_OpensTheGate()
        {
            List<TrackSection> sections = RoverSurfaceSections();
            var memo = default(WheelGroundContactMemo);

            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1050.0, ref memo));  // parked
            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1500.0, ref memo));  // driving
        }

        [Fact]
        public void ResolveWheelGroundContact_TouchdownBoundary_FlipsExactlyAtTheSectionSeam()
        {
            // Atmospheric [1000, 2000) then SurfaceMobile [2000, 3000]: an aircraft landing. The
            // recorder's own LANDED->Surface classification IS the wheels-on-ground boundary.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.Atmospheric, 1000.0, 2000.0),
                Section(SegmentEnvironment.SurfaceMobile, 2000.0, 3000.0)
            };
            var memo = default(WheelGroundContactMemo);

            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1999.999, ref memo));
            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 2000.0, ref memo));
            // ...and back the other way, which is what proves the memo invalidates on a section change
            // rather than pinning the first answer for the ghost's life.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1500.0, ref memo));
        }

        [Fact]
        public void ResolveWheelGroundContact_NoCoveringSection_FailsClosed()
        {
            List<TrackSection> sections = RoverSurfaceSections();
            var memo = default(WheelGroundContactMemo);

            // Before the recorded span, after it, and the empty / null lists a BG on-rails recording
            // and a re-aimed trajectory present by contract. Every one holds the wheels still.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1.0, ref memo));
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 99999.0, ref memo));
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(
                new List<TrackSection>(), 1500.0, ref memo));
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(null, 1500.0, ref memo));
        }

        [Fact]
        public void ResolveWheelGroundContact_MemoIsWindowedToTheResolvedSection_AndClearedOnAMiss()
        {
            List<TrackSection> sections = RoverSurfaceSections();  // [1000,1200) stationary, [1200,2000] mobile
            var memo = default(WheelGroundContactMemo);

            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1050.0, ref memo));
            Assert.True(memo.hasValue);
            Assert.Equal(1000.0, memo.startUT);
            Assert.Equal(1200.0, memo.endUT);
            Assert.True(memo.onGround);

            // A UT outside every section clears the memo rather than leaving a stale window that a
            // later probe inside it could read.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 5.0, ref memo));
            Assert.False(memo.hasValue);
        }

        [Fact]
        public void ResolveWheelGroundContact_CachedWindowAnswersWithoutRescanning()
        {
            // The per-frame budget claim: a steady-state frame must not re-walk the section list.
            // Proven by mutating the list underneath a valid memo — a cached read cannot see the
            // change; a rescan would.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.SurfaceMobile, 1000.0, 2000.0)
            };
            var memo = default(WheelGroundContactMemo);
            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1500.0, ref memo));

            sections[0] = Section(SegmentEnvironment.ExoBallistic, 1000.0, 2000.0);
            Assert.True(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 1600.0, ref memo));

            // Stepping outside the window rescans and now sees the mutated class.
            Assert.False(GhostPlaybackLogic.ResolveWheelGroundContact(sections, 2000.0, ref memo));
        }

        private static TrackSection Section(SegmentEnvironment env, double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        private static List<TrackSection> AscentSections()
        {
            return new List<TrackSection>
            {
                Section(SegmentEnvironment.SurfaceStationary, 100.0, 200.0),  // on the pad
                Section(SegmentEnvironment.Atmospheric, 200.0, 400.0),        // ascent
                Section(SegmentEnvironment.ExoPropulsive, 400.0, 500.0),      // circularisation burn
                Section(SegmentEnvironment.ExoBallistic, 500.0, 1000.0)       // parking orbit
            };
        }

        private static List<TrackSection> RoverSurfaceSections()
        {
            return new List<TrackSection>
            {
                Section(SegmentEnvironment.SurfaceStationary, 1000.0, 1200.0),
                Section(SegmentEnvironment.SurfaceMobile, 1200.0, 2000.0)
            };
        }

        #endregion

        #region End to end — a coasting rover, the case the old signal got backwards

        [Fact]
        public void CoastingRover_SpinsItsWheels_WhereTheRecordedTorqueSignalShowedThemStationary()
        {
            // driveOutput is Abs(driveInput * maxDriveTorque / maxTorque) * 100 * resourceFraction,
            // so a rover rolling with no motor input recorded 0 and playback froze the wheels. The
            // derivation has no such dependence: motion alone is enough.
            var up = new Vector3(0f, 1f, 0f);
            var bodyRotation = new Vector3(175f, 0f, 0f);
            var world = bodyRotation + new Vector3(0f, 0f, 8f); // coasting at 8 m/s

            Vector3 horiz = GhostPlaybackLogic.ComputeSurfaceHorizontalVelocity(world, bodyRotation, up);
            Vector3 roll = GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(1f, 0f, 0f), up);
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(horiz, roll, 0.4f, 1.0);

            Assert.True(Mathf.Abs(deg) > 1f, $"coasting wheels must turn, got {deg} deg/s");
            // 8 m/s / 0.4 m = 20 rad/s, regardless of which way the axis points.
            Assert.Equal(20.0 * Mathf.Rad2Deg, (double)Mathf.Abs(deg), 1);
        }

        #endregion
    }
}
