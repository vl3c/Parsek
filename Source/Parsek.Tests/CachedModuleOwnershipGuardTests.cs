using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P4a / audit M5 — the vessel-identity guard on the recorder's cached engine / RCS / robotic
    /// module lists.
    ///
    /// The caches hold direct <c>Part</c> / <c>PartModule</c> references captured at
    /// <c>StartRecording</c>. A staged-away booster's <c>Part</c> is NOT destroyed: KSP moves it to
    /// a brand-new debris <c>Vessel</c> and it keeps burning. The pre-fix poll only tested
    /// <c>part == null</c>, so that booster kept writing EngineIgnited / EngineThrottle /
    /// EngineShutdown into the PARENT recording under the booster's own part pid.
    ///
    /// These cells cover the pure decision core. The live wrapper feeds it
    /// <c>ReferenceEquals(part.vessel, recordedVessel)</c> — a live in-memory object comparison
    /// within one session, not a persistentId or guid comparison, so the craft-baked-pid identity
    /// rule does not apply.
    /// </summary>
    public class CachedModuleOwnershipGuardTests
    {
        [Fact]
        public void APartStillOnTheRecordedVesselIsPolled()
        {
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.Poll,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void AStagedAwayBoosterStillHoldingALiveEngineIsNotPolled()
        {
            // The exact M5 shape: part alive, module alive, vessel alive — but it is the debris
            // vessel, not ours.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipForeignVessel,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: true, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void APartWhoseVesselIsNullMidSplitIsNotPolled()
        {
            // One-frame window between the decoupler firing and KSP reparenting the part.
            // "Probably still ours" is precisely the guess that produced the bug.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipForeignVessel,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: false, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void ADestroyedPartIsReportedAsANullEntryNotAsAForeignVessel()
        {
            // The two skip reasons are distinct because only the foreign-vessel one is logged:
            // a destroyed part is routine, a foreign part means events were being misattributed.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: false, hasModule: true,
                    hasPartVessel: false, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void ADestroyedModuleOnASurvivingPartIsANullEntry()
        {
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: false,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void TheNullEntryVerdictWinsEvenWhenTheVesselStillMatches()
        {
            // Ordering matters: a null module with a matching vessel must not read as Poll.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: false, hasModule: false,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void OnlyTheAllFourTrueCombinationPolls()
        {
            // Exhaustive truth table — 16 combinations, exactly one Poll.
            int pollCount = 0;
            for (int mask = 0; mask < 16; mask++)
            {
                var decision = FlightRecorder.DecideCachedModulePoll(
                    (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
                if (decision == FlightRecorder.CachedModulePollDecision.Poll)
                    pollCount++;
            }
            Assert.Equal(1, pollCount);
        }
    }

    /// <summary>
    /// The other half of M5: because the ownership guard makes a departed booster's burnout
    /// UNOBSERVABLE, its key never leaves <c>activeEngineKeys</c> on its own. The terminal emit
    /// (rails transition, and recording stop) walks the ACTIVE sets, so without a prune it writes an
    /// EngineShutdown into the PARENT recording for a pid that left minutes earlier — at the TAIL,
    /// where <see cref="RecordingOptimizer.IsInertPartEventForTailTrim"/> counts EngineShutdown as
    /// interesting and the #263 boring-tail trim is defeated.
    /// </summary>
    public class DepartedTrackingKeyPruneTests
    {
        private const uint PodPid = 10u;
        private const uint BoosterPid = 20u;

        private static ulong Key(uint pid, int midx) => FlightRecorder.EncodeEngineKey(pid, midx);

        /// <summary>Mutable stand-in for the recorder's eight cache-fed tracking collections.</summary>
        private sealed class Sets
        {
            public readonly HashSet<ulong> activeEngineKeys = new HashSet<ulong>();
            public readonly Dictionary<ulong, float> lastThrottle = new Dictionary<ulong, float>();
            public readonly HashSet<ulong> allEngineKeys = new HashSet<ulong>();
            public readonly HashSet<ulong> activeRcsKeys = new HashSet<ulong>();
            public readonly Dictionary<ulong, float> lastRcsThrottle = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, int> rcsActiveFrameCount = new Dictionary<ulong, int>();
            public readonly HashSet<ulong> activeRoboticKeys = new HashSet<ulong>();
            public readonly Dictionary<ulong, float> lastRoboticPosition = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, double> lastRoboticSampleUT = new Dictionary<ulong, double>();
            public readonly Dictionary<ulong, float> departedEngineThrottles = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, float> departedRcsThrottles = new Dictionary<ulong, float>();

            public FlightRecorder.DepartedKeyPruneResult Prune(params uint[] survivingPids)
            {
                return FlightRecorder.PruneDepartedTrackingKeys(
                    new HashSet<uint>(survivingPids),
                    activeEngineKeys, lastThrottle, allEngineKeys,
                    activeRcsKeys, lastRcsThrottle, rcsActiveFrameCount,
                    activeRoboticKeys, lastRoboticPosition, lastRoboticSampleUT,
                    departedEngineThrottles, departedRcsThrottles);
            }
        }

        /// <summary>A pod engine idle at zero and a booster engine at full throttle.</summary>
        private static Sets StagedRocket()
        {
            var s = new Sets();
            s.allEngineKeys.Add(Key(PodPid, 0));
            s.allEngineKeys.Add(Key(BoosterPid, 0));
            s.activeEngineKeys.Add(Key(BoosterPid, 0));
            s.lastThrottle[Key(BoosterPid, 0)] = 1f;
            return s;
        }

        // --- the review's launch / stage / coast / stop scenario ---

        [Fact]
        public void AStagedAwayBoosterGetsNoTerminalShutdownInTheParentRecording()
        {
            var s = StagedRocket();

            // T=200: the booster separates. KSP fires onVesselWasModified; only the pod survives.
            var pruned = s.Prune(PodPid);
            Assert.Equal(1, pruned.engineKeys);

            // T=600: the pod's recording stops after a long quiet coast.
            var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                s.activeEngineKeys, s.activeRcsKeys, s.activeRoboticKeys,
                s.lastRoboticPosition, 600.0, "Recorder");

            Assert.Empty(terminals);
            Assert.DoesNotContain(terminals, e => e.partPersistentId == BoosterPid);
        }

        [Fact]
        public void TheBoringTailTrimWindowIsNotExtendedByAStaleShutdown()
        {
            var s = StagedRocket();

            var rec = new Recording();
            // The real activity: the booster lit at T=0 and separated at T=200.
            rec.PartEvents.Add(new PartEvent
            {
                ut = 0.0,
                partPersistentId = BoosterPid,
                eventType = PartEventType.EngineIgnited,
                value = 1f
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 200.0,
                partPersistentId = BoosterPid,
                eventType = PartEventType.Decoupled
            });

            s.Prune(PodPid);
            rec.PartEvents.AddRange(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                s.activeEngineKeys, s.activeRcsKeys, s.activeRoboticKeys,
                s.lastRoboticPosition, 600.0, "Recorder"));

            // Four hundred seconds of boring coast stay trimmable.
            Assert.Equal(200.0, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void WithoutThePruneTheSameStaleShutdownPinsTheTailToTheEnd()
        {
            // The pre-fix shape, stated so the cell above is measuring something. EngineShutdown is
            // deliberately non-inert for tail trim, so one stale terminal is enough.
            var s = StagedRocket();

            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 200.0,
                partPersistentId = BoosterPid,
                eventType = PartEventType.Decoupled
            });
            rec.PartEvents.AddRange(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                s.activeEngineKeys, s.activeRcsKeys, s.activeRoboticKeys,
                s.lastRoboticPosition, 600.0, "Recorder"));

            Assert.Equal(600.0, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        // --- the dock-shuffle survivor ---

        [Fact]
        public void APartStillOnTheVesselKeepsItsBurnEvenWhenItsModuleFellOutOfTheCache()
        {
            // The hazard the review named: a part momentarily missing from the derived cache lists
            // (or momentarily vesselless) during a dock/undock shuffle must not be pruned into
            // losing a genuinely-continuing burn. The prune measures survival against the pids the
            // caller collected — which is Vessel.parts UNIONED with the caches, not the caches
            // alone — so a part the vessel still lists survives a one-frame cache dropout.
            var s = new Sets();
            s.activeEngineKeys.Add(Key(PodPid, 0));
            s.lastThrottle[Key(PodPid, 0)] = 0.75f;
            s.allEngineKeys.Add(Key(PodPid, 0));

            s.Prune(PodPid);

            Assert.Contains(Key(PodPid, 0), s.activeEngineKeys);
            Assert.Equal(0.75f, s.lastThrottle[Key(PodPid, 0)]);
            Assert.Empty(s.departedEngineThrottles);

            var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                s.activeEngineKeys, s.activeRcsKeys, s.activeRoboticKeys,
                s.lastRoboticPosition, 600.0, "Recorder");
            Assert.Equal(PartEventType.EngineShutdown, Assert.Single(terminals).eventType);
        }

        [Fact]
        public void AnEmptySurvivingSetIsTreatedAsATransientReadAndPrunesNothing()
        {
            // A vessel that momentarily reads as having no parts is a mid-split artefact, not proof
            // that every tracked module left. onVesselWasModified fires again on the settled shape.
            var s = StagedRocket();

            var pruned = s.Prune();

            Assert.Equal(0, pruned.Total);
            Assert.Contains(Key(BoosterPid, 0), s.activeEngineKeys);
            Assert.False(FlightRecorder.CanPruneAgainstSurvivingPids(0));
            Assert.True(FlightRecorder.CanPruneAgainstSurvivingPids(1));
        }

        // --- every cache-fed family, not just the active sets ---

        [Fact]
        public void EveryCacheFedTrackingCollectionDropsTheDepartedPid()
        {
            var s = new Sets();
            ulong eng = Key(BoosterPid, 0), rcs = Key(BoosterPid, 1), rob = Key(BoosterPid, 2);
            s.activeEngineKeys.Add(eng);
            s.lastThrottle[eng] = 1f;
            s.allEngineKeys.Add(eng);
            s.activeRcsKeys.Add(rcs);
            s.lastRcsThrottle[rcs] = 0.5f;
            s.rcsActiveFrameCount[rcs] = 9;
            s.activeRoboticKeys.Add(rob);
            s.lastRoboticPosition[rob] = 42f;
            s.lastRoboticSampleUT[rob] = 123.0;

            var pruned = s.Prune(PodPid);

            Assert.Equal(1, pruned.engineKeys);
            Assert.Equal(1, pruned.rcsKeys);
            Assert.Equal(1, pruned.roboticKeys);
            Assert.Equal(3, pruned.Total);

            Assert.Empty(s.activeEngineKeys);
            Assert.Empty(s.lastThrottle);
            Assert.Empty(s.allEngineKeys);
            Assert.Empty(s.activeRcsKeys);
            Assert.Empty(s.lastRcsThrottle);
            Assert.Empty(s.rcsActiveFrameCount);
            Assert.Empty(s.activeRoboticKeys);
            Assert.Empty(s.lastRoboticPosition);
            Assert.Empty(s.lastRoboticSampleUT);
        }

        [Fact]
        public void ThePruneIsSilentAndWritesNoSyntheticEvent()
        {
            // The Decoupled event already hides the subtree at playback and the child recording owns
            // the burn, so the prune must not manufacture anything.
            var s = StagedRocket();
            var pruned = s.Prune(PodPid);

            Assert.Equal(1, pruned.Total);
            // The only surface a prune could write through is the terminal emit, and it is empty.
            Assert.Empty(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                s.activeEngineKeys, s.activeRcsKeys, s.activeRoboticKeys,
                s.lastRoboticPosition, 600.0, "Recorder"));
        }

        // --- the #298 carry-over: the prune must not delete what the deferred breakup snapshot reads ---

        [Fact]
        public void ADepartedBoostersThrottleSurvivesForTheDeferredBreakupSnapshot()
        {
            // ProcessBreakupEvent runs a whole crash-coalescer window AFTER onVesselWasModified, so
            // by the time #298 takes its snapshot the key is already out of the active set.
            var s = StagedRocket();
            s.Prune(PodPid);

            Assert.Equal(1f, s.departedEngineThrottles[Key(BoosterPid, 0)]);

            HashSet<ulong> keys;
            Dictionary<ulong, float> throttles;
            FlightRecorder.UnionDepartedIntoInheritedState(
                s.activeEngineKeys, s.lastThrottle, s.departedEngineThrottles,
                out keys, out throttles);

            Assert.Equal(new[] { Key(BoosterPid, 0) }, keys.ToArray());
            Assert.Equal(1f, throttles[Key(BoosterPid, 0)]);
        }

        [Fact]
        public void TheLivePollWinsOverACarryOverEntryForTheSameKey()
        {
            var s = new Sets();
            s.activeEngineKeys.Add(Key(PodPid, 0));
            s.lastThrottle[Key(PodPid, 0)] = 0.3f;
            s.departedEngineThrottles[Key(PodPid, 0)] = 1f;

            HashSet<ulong> keys;
            Dictionary<ulong, float> throttles;
            FlightRecorder.UnionDepartedIntoInheritedState(
                s.activeEngineKeys, s.lastThrottle, s.departedEngineThrottles,
                out keys, out throttles);

            Assert.Equal(0.3f, throttles[Key(PodPid, 0)]);
        }

        [Fact]
        public void AnEmptyUnionStaysNullSoNothingWasRunningKeepsItsMeaning()
        {
            HashSet<ulong> keys;
            Dictionary<ulong, float> throttles;
            FlightRecorder.UnionDepartedIntoInheritedState(
                new HashSet<ulong>(), new Dictionary<ulong, float>(), new Dictionary<ulong, float>(),
                out keys, out throttles);

            Assert.Null(keys);
            Assert.Null(throttles);
        }

        [Fact]
        public void APidThatComesBackClearsItsOwnCarryOver()
        {
            // Undock, then re-dock the same tug. The live poll owns its state again from here.
            var s = StagedRocket();
            s.Prune(PodPid);
            Assert.Single(s.departedEngineThrottles);

            s.Prune(PodPid, BoosterPid);

            Assert.Empty(s.departedEngineThrottles);
        }

        [Fact]
        public void RoboticKeysGetNoCarryOverBecauseTheyHaveNoInheritancePath()
        {
            var s = new Sets();
            s.activeRoboticKeys.Add(Key(BoosterPid, 0));
            s.lastRoboticPosition[Key(BoosterPid, 0)] = 42f;

            s.Prune(PodPid);

            Assert.Empty(s.activeRoboticKeys);
            Assert.Empty(s.departedEngineThrottles);
            Assert.Empty(s.departedRcsThrottles);
        }

        [Fact]
        public void ANullCollectionSetPrunesNothingRatherThanThrowing()
        {
            var result = FlightRecorder.PruneDepartedTrackingKeys(
                null, null, null, null, null, null, null, null, null, null, null, null);
            Assert.Equal(0, result.Total);

            result = FlightRecorder.PruneDepartedTrackingKeys(
                new HashSet<uint> { PodPid },
                null, null, null, null, null, null, null, null, null, null, null);
            Assert.Equal(0, result.Total);
        }
    }
}
