using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P4b / audit M6 — the background rails-span reconciler.
    ///
    /// A BG vessel that packs onto rails used to have its <c>loadedStates</c> entry dropped with no
    /// terminal emit (so a ghost's plume stayed lit for the whole span), and on re-entry
    /// <c>SeedBackgroundPartStates</c> re-synced every tracking set to live truth while
    /// <c>TrySeedLoadedPartEvents</c> declined to write anything — so a change that happened across
    /// the warp was ERASED, not deferred. <see cref="PartStateSeeder.EmitDiffEvents"/> is the pure
    /// reconciler that turns that erasure into the two-to-four events it should always have been.
    /// </summary>
    [Collection("Sequential")]
    public class RailsSpanPartStateDiffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RailsSpanPartStateDiffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static List<PartEvent> Diff(PartTrackingSets before, PartTrackingSets after)
        {
            return PartStateSeeder.EmitDiffEvents(
                before, after, new Dictionary<uint, string> { { 7u, "probeCoreOcto" } },
                1000.0, "BgRecorder");
        }

        private static ulong Key(uint pid, int midx) => FlightRecorder.EncodeEngineKey(pid, midx);

        // --- the null case: nothing changed across the warp ---

        [Fact]
        public void IdenticalStateEmitsNothing()
        {
            var before = new PartTrackingSets();
            before.extendedDeployables.Add(7u);
            before.activeEngineKeys.Add(Key(7u, 0));
            before.lastThrottle[Key(7u, 0)] = 0.8f;

            var after = PartStateSeeder.ClonePartTrackingSets(before);

            Assert.Empty(Diff(before, after));
        }

        [Fact]
        public void ANullSideEmitsNothingRatherThanThrowing()
        {
            Assert.Empty(Diff(null, new PartTrackingSets()));
            Assert.Empty(Diff(new PartTrackingSets(), null));
        }

        // --- the clone contract: the snapshot must not track the live sets ---

        [Fact]
        public void TheCloneIsDeepSoLaterMutationOfTheOriginalDoesNotFollowIt()
        {
            var live = new PartTrackingSets();
            live.lightsOn.Add(7u);
            live.lastThrottle[Key(7u, 0)] = 0.5f;

            var snapshot = PartStateSeeder.ClonePartTrackingSets(live);

            // The live sets keep mutating for the rest of the flight; the snapshot must not.
            live.lightsOn.Remove(7u);
            live.lastThrottle[Key(7u, 0)] = 0.9f;

            Assert.Contains(7u, snapshot.lightsOn);
            Assert.Equal(0.5f, snapshot.lastThrottle[Key(7u, 0)]);

            // And it is a real diff source: the light going out across the span is now visible.
            var events = Diff(snapshot, live);
            Assert.Single(events);
            Assert.Equal(PartEventType.LightOff, events[0].eventType);
        }

        // --- reversible pid-keyed families, both directions ---

        [Theory]
        [InlineData("extendedDeployables", PartEventType.DeployableExtended, PartEventType.DeployableRetracted)]
        [InlineData("lightsOn", PartEventType.LightOn, PartEventType.LightOff)]
        [InlineData("deployedGear", PartEventType.GearDeployed, PartEventType.GearRetracted)]
        [InlineData("openCargoBays", PartEventType.CargoBayOpened, PartEventType.CargoBayClosed)]
        public void AReversibleFamilyEmitsInBothDirections(
            string setName, PartEventType onArrival, PartEventType onDeparture)
        {
            HashSet<uint> Pick(PartTrackingSets s)
            {
                switch (setName)
                {
                    case "extendedDeployables": return s.extendedDeployables;
                    case "lightsOn": return s.lightsOn;
                    case "deployedGear": return s.deployedGear;
                    default: return s.openCargoBays;
                }
            }

            var empty = new PartTrackingSets();
            var populated = new PartTrackingSets();
            Pick(populated).Add(7u);

            var arrived = Diff(empty, populated);
            Assert.Single(arrived);
            Assert.Equal(onArrival, arrived[0].eventType);
            Assert.Equal(7u, arrived[0].partPersistentId);
            Assert.Equal("probeCoreOcto", arrived[0].partName);
            Assert.Equal(1000.0, arrived[0].ut);

            var departed = Diff(populated, empty);
            Assert.Single(departed);
            Assert.Equal(onDeparture, departed[0].eventType);
        }

        // --- one-way families: arrival only ---

        [Fact]
        public void AJettisonedShroudEmitsOnArrivalAndNothingOnDeparture()
        {
            var empty = new PartTrackingSets();
            var jettisoned = new PartTrackingSets();
            jettisoned.jettisonedShrouds.Add(7u);

            Assert.Equal(
                PartEventType.ShroudJettisoned,
                Assert.Single(Diff(empty, jettisoned)).eventType);

            // A pid leaving the set means the part is gone — a decouple/destroy the recorder covers
            // through its own GameEvents, NOT an un-jettison. There is no such event type.
            Assert.Empty(Diff(jettisoned, empty));
        }

        [Fact]
        public void ADeployedFairingEmitsOnArrivalAndNothingOnDeparture()
        {
            var empty = new PartTrackingSets();
            var deployed = new PartTrackingSets();
            deployed.deployedFairings.Add(7u);

            Assert.Equal(
                PartEventType.FairingJettisoned,
                Assert.Single(Diff(empty, deployed)).eventType);
            Assert.Empty(Diff(deployed, empty));
        }

        // --- blink: three-valued ---

        [Fact]
        public void BlinkEnableDisableAndRateChangeAreAllDistinguished()
        {
            var off = new PartTrackingSets();
            var on = new PartTrackingSets();
            on.blinkingLights.Add(7u);
            on.lightBlinkRates[7u] = 2f;

            var enabled = Assert.Single(Diff(off, on));
            Assert.Equal(PartEventType.LightBlinkEnabled, enabled.eventType);
            Assert.Equal(2f, enabled.value);

            var disabled = Assert.Single(Diff(on, off));
            Assert.Equal(PartEventType.LightBlinkDisabled, disabled.eventType);

            var faster = PartStateSeeder.ClonePartTrackingSets(on);
            faster.lightBlinkRates[7u] = 5f;
            var rateChanged = Assert.Single(Diff(on, faster));
            Assert.Equal(PartEventType.LightBlinkRate, rateChanged.eventType);
            Assert.Equal(5f, rateChanged.value);
        }

        [Fact]
        public void ABlinkRateThatBarelyMovedEmitsNothing()
        {
            var before = new PartTrackingSets();
            before.blinkingLights.Add(7u);
            before.lightBlinkRates[7u] = 2f;
            var after = PartStateSeeder.ClonePartTrackingSets(before);
            after.lightBlinkRates[7u] = 2.005f;

            Assert.Empty(Diff(before, after));
        }

        // --- parachutes route through the shared 4-state table ---

        [Fact]
        public void AChuteDeployedAcrossTheSpanEmitsParachuteDeployed()
        {
            var before = new PartTrackingSets();
            var after = new PartTrackingSets();
            after.parachuteStates[7u] = FlightRecorder.ParachuteStateDeployed;

            Assert.Equal(
                PartEventType.ParachuteDeployed,
                Assert.Single(Diff(before, after)).eventType);
        }

        [Fact]
        public void ARepackAcrossTheSpanEmitsParachuteRepackedNotParachuteCut()
        {
            // The absent-means-Stowed convention plus ClassifyParachuteTransitionEvent: Cut ->
            // (absent = Stowed) is the repack, and it must not degrade to a second cut.
            var before = new PartTrackingSets();
            before.parachuteStates[7u] = FlightRecorder.ParachuteStateCut;
            var after = new PartTrackingSets();

            Assert.Equal(
                PartEventType.ParachuteRepacked,
                Assert.Single(Diff(before, after)).eventType);
        }

        [Fact]
        public void AChuteThatDidNotMoveEmitsNothingEvenWhenItIsCut()
        {
            var before = new PartTrackingSets();
            before.parachuteStates[7u] = FlightRecorder.ParachuteStateCut;
            var after = PartStateSeeder.ClonePartTrackingSets(before);

            Assert.Empty(Diff(before, after));
        }

        // --- module-keyed deployable families keep their moduleIndex ---

        [Fact]
        public void AModuleKeyedDeployableCarriesItsDecodedModuleIndex()
        {
            var before = new PartTrackingSets();
            var after = new PartTrackingSets();
            after.deployedControlSurfaceModules.Add(Key(7u, 3));

            var evt = Assert.Single(Diff(before, after));
            Assert.Equal(PartEventType.DeployableExtended, evt.eventType);
            Assert.Equal(7u, evt.partPersistentId);
            Assert.Equal(3, evt.moduleIndex);
        }

        [Fact]
        public void EveryModuleKeyedDeployableFamilyIsCovered()
        {
            // A family silently missing from the reconciler would erase exactly what M6 is about.
            var families = new List<Action<PartTrackingSets>>
            {
                s => s.deployedLadders.Add(Key(7u, 1)),
                s => s.deployedAnimationGroups.Add(Key(7u, 1)),
                s => s.deployedAnimateGenericModules.Add(Key(7u, 1)),
                s => s.deployedAeroSurfaceModules.Add(Key(7u, 1)),
                s => s.deployedControlSurfaceModules.Add(Key(7u, 1)),
                s => s.deployedRobotArmScannerModules.Add(Key(7u, 1)),
            };

            foreach (var populate in families)
            {
                var after = new PartTrackingSets();
                populate(after);
                Assert.Equal(
                    PartEventType.DeployableExtended,
                    Assert.Single(Diff(new PartTrackingSets(), after)).eventType);
                Assert.Equal(
                    PartEventType.DeployableRetracted,
                    Assert.Single(Diff(after, new PartTrackingSets())).eventType);
            }
        }

        // --- thermal: absent means Cold ---

        [Fact]
        public void HeatLevelChangesEmitTheMatchingThermalEventIncludingTheReturnToCold()
        {
            var cold = new PartTrackingSets();
            var hot = new PartTrackingSets();
            hot.animateHeatLevels[Key(7u, 2)] = HeatLevel.Hot;

            var heated = Assert.Single(Diff(cold, hot));
            Assert.Equal(PartEventType.ThermalAnimationHot, heated.eventType);
            Assert.Equal(2, heated.moduleIndex);

            // Absent from the map == Cold, so cooling off across the span is a real transition.
            Assert.Equal(
                PartEventType.ThermalAnimationCold,
                Assert.Single(Diff(hot, cold)).eventType);

            var medium = new PartTrackingSets();
            medium.animateHeatLevels[Key(7u, 2)] = HeatLevel.Medium;
            Assert.Equal(
                PartEventType.ThermalAnimationMedium,
                Assert.Single(Diff(hot, medium)).eventType);
        }

        // --- engines: the headline case ---

        [Fact]
        public void AnEngineStillBurningAfterTheRailsSpanReIgnites()
        {
            // The rails-entry terminal emit left the snapshot engine-quiet; the vessel comes back
            // still burning, so the recording must be told to relight the plume.
            var beforeQuiet = new PartTrackingSets();
            var afterBurning = new PartTrackingSets();
            afterBurning.activeEngineKeys.Add(Key(7u, 0));
            afterBurning.lastThrottle[Key(7u, 0)] = 0.75f;

            var evt = Assert.Single(Diff(beforeQuiet, afterBurning));
            Assert.Equal(PartEventType.EngineIgnited, evt.eventType);
            Assert.Equal(0.75f, evt.value);
        }

        [Fact]
        public void AnEngineThatStoppedAcrossTheSpanEmitsEngineShutdown()
        {
            var burning = new PartTrackingSets();
            burning.activeEngineKeys.Add(Key(7u, 0));
            burning.lastThrottle[Key(7u, 0)] = 0.75f;

            Assert.Equal(
                PartEventType.EngineShutdown,
                Assert.Single(Diff(burning, new PartTrackingSets())).eventType);
        }

        [Fact]
        public void AnIdleZeroThrottleEngineIsNotRelitAcrossTheSpan()
        {
            // #165: EngineIgnited(0) flashes a plume on and immediately off at playback.
            var before = new PartTrackingSets();
            var after = new PartTrackingSets();
            after.activeEngineKeys.Add(Key(7u, 0));
            after.lastThrottle[Key(7u, 0)] = 0f;

            Assert.Empty(Diff(before, after));
        }

        [Fact]
        public void AThrottleMoveBiggerThanTheDeadbandEmitsEngineThrottle()
        {
            var before = new PartTrackingSets();
            before.activeEngineKeys.Add(Key(7u, 0));
            before.lastThrottle[Key(7u, 0)] = 0.30f;

            var after = PartStateSeeder.ClonePartTrackingSets(before);
            after.lastThrottle[Key(7u, 0)] = 0.90f;

            var evt = Assert.Single(Diff(before, after));
            Assert.Equal(PartEventType.EngineThrottle, evt.eventType);
            Assert.Equal(0.90f, evt.value);
        }

        [Fact]
        public void AThrottleMoveInsideTheDeadbandEmitsNothing()
        {
            var before = new PartTrackingSets();
            before.activeEngineKeys.Add(Key(7u, 0));
            before.lastThrottle[Key(7u, 0)] = 0.30f;

            var after = PartStateSeeder.ClonePartTrackingSets(before);
            after.lastThrottle[Key(7u, 0)] = 0.30f + FlightRecorder.EngineThrottleDeadband * 0.5f;

            Assert.Empty(Diff(before, after));
        }

        // --- RCS ---

        [Fact]
        public void RcsStartAndStopAcrossTheSpanAreBothRecorded()
        {
            var idle = new PartTrackingSets();
            var firing = new PartTrackingSets();
            firing.activeRcsKeys.Add(Key(7u, 1));
            firing.lastRcsThrottle[Key(7u, 1)] = 0.4f;

            var started = Assert.Single(Diff(idle, firing));
            Assert.Equal(PartEventType.RCSActivated, started.eventType);
            Assert.Equal(0.4f, started.value);
            Assert.Equal(1, started.moduleIndex);

            Assert.Equal(
                PartEventType.RCSStopped,
                Assert.Single(Diff(firing, idle)).eventType);
        }

        // --- logging ---

        [Fact]
        public void ANonEmptyReconcileIsLoggedWithItsCount()
        {
            var before = new PartTrackingSets();
            var after = new PartTrackingSets();
            after.deployedGear.Add(7u);
            after.lightsOn.Add(7u);

            var events = Diff(before, after);
            Assert.Equal(2, events.Count);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]") && l.Contains("Rails-span reconcile: 2 event(s)"));
            Assert.Contains(logLines, l => l.Contains("Rails-span event: GearDeployed"));
        }

        [Fact]
        public void AnEmptyReconcileLogsNothing()
        {
            var sets = new PartTrackingSets();
            sets.lightsOn.Add(7u);
            Diff(sets, PartStateSeeder.ClonePartTrackingSets(sets));

            Assert.DoesNotContain(logLines, l => l.Contains("Rails-span reconcile"));
        }

        // --- every emitted event is stamped at the reconcile UT ---

        [Fact]
        public void EveryEmittedEventCarriesTheReconcileUt()
        {
            var before = new PartTrackingSets();
            before.activeEngineKeys.Add(Key(7u, 0));
            before.lastThrottle[Key(7u, 0)] = 0.5f;

            var after = new PartTrackingSets();
            after.deployedGear.Add(7u);
            after.animateHeatLevels[Key(7u, 0)] = HeatLevel.Hot;

            var events = Diff(before, after);
            Assert.Equal(3, events.Count);
            Assert.All(events, e => Assert.Equal(1000.0, e.ut));
        }
    }
}
