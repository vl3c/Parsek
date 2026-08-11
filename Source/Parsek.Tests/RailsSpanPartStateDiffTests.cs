using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Drift proofing for the M6 reconciler. The hand-written cells above enumerate families by
    /// name, which is exactly the thing that goes stale when a 22nd field lands on
    /// <see cref="PartTrackingSets"/>: the clone silently aliases it, or the diff silently ignores
    /// it, and the only symptom is state quietly erased across a rails span again.
    ///
    /// <para>
    /// Both sweeps reflect over the real field list, so adding a field to
    /// <see cref="PartTrackingSets"/> without teaching the clone and the diff about it reds HERE
    /// rather than in a playtest.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class PartTrackingSetsFieldSweepTests : IDisposable
    {
        public PartTrackingSetsFieldSweepTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Fields <see cref="PartStateSeeder.EmitDiffEvents"/> deliberately does NOT reconcile, with
        /// the reason it is not a diffable state. Adding to this list is a deliberate act; a field
        /// that lands here by accident is the defect the sweep exists to catch.
        /// </summary>
        private static readonly Dictionary<string, string> DiffExemptFields =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                {
                    "allEngineKeys",
                    "Not a state: it is the #298 roster of engines PRESENT on the vessel, the source " +
                    "for EmitSeedEvents' EngineShutdown sentinels. Diffing it would emit a second " +
                    "shutdown for every engine DiffEngines already reported, and its own membership " +
                    "changes are part arrivals/departures, which the recorder covers via GameEvents."
                },
            };

        private static FieldInfo[] Fields()
        {
            var fields = typeof(PartTrackingSets)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToArray();
            // A canary: if the reflection ever stops seeing the fields, every sweep below would
            // vacuously pass.
            Assert.True(fields.Length >= 21,
                $"Expected PartTrackingSets to expose at least 21 tracking collections, saw {fields.Length}");
            return fields;
        }

        // ---- sweep 1: the clone is deep for EVERY field ----

        [Fact]
        public void TheCloneCopiesEveryFieldIntoADistinctCollectionInstance()
        {
            foreach (FieldInfo f in Fields())
            {
                var live = new PartTrackingSets();
                object liveCollection = f.GetValue(live);
                Assert.True(liveCollection != null, $"{f.Name} is null on a fresh PartTrackingSets");
                Populate(liveCollection, variantB: false);
                Assert.Equal(1, Count(liveCollection));

                var snapshot = PartStateSeeder.ClonePartTrackingSets(live);
                object clonedCollection = f.GetValue(snapshot);

                Assert.True(clonedCollection != null, $"{f.Name} came back null from the clone");
                Assert.False(ReferenceEquals(liveCollection, clonedCollection),
                    $"ClonePartTrackingSets aliases '{f.Name}' — the snapshot would track the live " +
                    "sets for the whole rails span and diff to nothing on re-entry");
                Assert.Equal(1, Count(clonedCollection));

                // The live sets keep mutating for the rest of the flight; the snapshot must not.
                Clear(liveCollection);
                Assert.Equal(0, Count(liveCollection));
                Assert.Equal(1, Count(clonedCollection));
            }
        }

        [Fact]
        public void TheCloneToleratesEveryFieldBeingNull()
        {
            var live = new PartTrackingSets();
            foreach (FieldInfo f in Fields())
                f.SetValue(live, null);

            var snapshot = PartStateSeeder.ClonePartTrackingSets(live);

            foreach (FieldInfo f in Fields())
                Assert.True(f.GetValue(snapshot) != null,
                    $"'{f.Name}' came back null from a clone of an all-null source");
        }

        // ---- sweep 2: the diff is SENSITIVE to every non-exempt field ----

        [Fact]
        public void TheReconcilerIsSensitiveToEveryNonExemptField()
        {
            // Probe: a diff from empty against a fully-populated state, versus the same diff with
            // exactly one field perturbed. If the two outputs are identical, the reconciler is not
            // reading that field at all — the family is silently missing.
            string baseline = Render(DiffFrom(BuildPopulated(perturbField: null)));

            foreach (FieldInfo f in Fields())
            {
                string perturbed = Render(DiffFrom(BuildPopulated(perturbField: f)));
                bool sensitive = !string.Equals(baseline, perturbed, StringComparison.Ordinal);

                if (DiffExemptFields.ContainsKey(f.Name))
                {
                    Assert.False(sensitive,
                        $"'{f.Name}' is on the diff-exempt list but the reconciler now reads it. " +
                        "Either the exemption is stale (drop it) or the new read is a double-emit.");
                    continue;
                }

                Assert.True(sensitive,
                    $"PartStateSeeder.EmitDiffEvents ignores '{f.Name}'. A change to it across a " +
                    "rails span would be ERASED, not deferred — the exact M6 defect. Add it to the " +
                    "reconciler, or to DiffExemptFields with a written reason.");
            }
        }

        [Fact]
        public void EveryDiffExemptionNamesARealField()
        {
            var names = new HashSet<string>(Fields().Select(f => f.Name), StringComparer.Ordinal);
            foreach (var kvp in DiffExemptFields)
            {
                Assert.True(names.Contains(kvp.Key), $"Stale diff exemption for '{kvp.Key}'");
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value),
                    $"Exemption for '{kvp.Key}' carries no reason");
            }
        }

        // ---- helpers ----

        private static List<PartEvent> DiffFrom(PartTrackingSets after)
        {
            return PartStateSeeder.EmitDiffEvents(
                new PartTrackingSets(), after, new Dictionary<uint, string>(), 1000.0, "BgRecorder");
        }

        private static PartTrackingSets BuildPopulated(FieldInfo perturbField)
        {
            var sets = new PartTrackingSets();
            foreach (FieldInfo f in Fields())
            {
                object collection = f.GetValue(sets);
                if (perturbField != null && f.Name == perturbField.Name)
                {
                    // Sets perturb by going empty; maps perturb by carrying a different value under
                    // the same key (an emptied map is indistinguishable from the implicit default
                    // for several families, which would make the sweep vacuous).
                    if (IsDictionary(collection)) Populate(collection, variantB: true);
                    continue;
                }
                Populate(collection, variantB: false);
            }
            return sets;
        }

        private static string Render(List<PartEvent> events)
        {
            return string.Join("|", events
                .Select(e => $"{e.eventType}:{e.partPersistentId}:{e.moduleIndex}:{e.value:R}")
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        private static bool IsDictionary(object collection)
        {
            return collection.GetType().GetGenericTypeDefinition() == typeof(Dictionary<,>);
        }

        private static void Populate(object collection, bool variantB)
        {
            Type t = collection.GetType();
            Type[] args = t.GetGenericArguments();
            if (t.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                t.GetMethod("Add").Invoke(collection, new[] { SampleKey(args[0]) });
                return;
            }
            t.GetMethod("set_Item").Invoke(
                collection, new[] { SampleKey(args[0]), SampleValue(args[1], variantB) });
        }

        private static void Clear(object collection)
        {
            collection.GetType().GetMethod("Clear").Invoke(collection, null);
        }

        private static int Count(object collection)
        {
            return (int)collection.GetType().GetProperty("Count").GetValue(collection, null);
        }

        private static object SampleKey(Type keyType)
        {
            // pid 7, module index 1 — the same shape the hand-written cells use.
            if (keyType == typeof(uint)) return 7u;
            if (keyType == typeof(ulong)) return FlightRecorder.EncodeEngineKey(7u, 1);
            throw new NotSupportedException($"Unhandled PartTrackingSets key type {keyType}");
        }

        private static object SampleValue(Type valueType, bool variantB)
        {
            // Both variants must be non-default and mutually distinct, or a perturbation collapses
            // onto the map's implicit default and the sweep reads as insensitive.
            if (valueType == typeof(int))
                return variantB
                    ? FlightRecorder.ParachuteStateDeployed
                    : FlightRecorder.ParachuteStateSemiDeployed;
            if (valueType == typeof(float)) return variantB ? 2.0f : 0.5f;
            if (valueType == typeof(double)) return variantB ? 200.0 : 100.0;
            if (valueType == typeof(HeatLevel)) return variantB ? HeatLevel.Medium : HeatLevel.Hot;
            throw new NotSupportedException($"Unhandled PartTrackingSets value type {valueType}");
        }
    }

    /// <summary>
    /// The rails-span snapshot is keyed on the vessel persistentId, which is craft-baked rather than
    /// launch-unique: a later launch of the SAME craft reuses the pid verbatim. An orphan left
    /// behind at a teardown site would therefore be handed to that launch's first off-rails
    /// re-entry and diffed as though it were the same continuous flight.
    ///
    /// <para>
    /// Source-text gate on the invariant, in the style of
    /// <c>GhostOrbitLineCascadeDeleteGateTests</c>: any method of <c>BackgroundRecorder.cs</c> that
    /// drops <c>loadedStates</c> must also drop <c>railsSpanPartStates</c>, with exactly one named
    /// exemption — the go-on-rails transition, which is where the snapshot is CAPTURED.
    /// </para>
    /// </summary>
    public class RailsSpanSnapshotTeardownGateTests
    {
        /// <summary>The one method that drops loadedStates by design without dropping the snapshot.</summary>
        private static readonly HashSet<string> CaptureSites =
            new HashSet<string>(StringComparer.Ordinal) { "OnBackgroundVesselGoOnRails" };

        [Fact]
        public void EveryLoadedStateTeardownAlsoDropsTheRailsSpanSnapshot()
        {
            string src = ReadParsekSource("BackgroundRecorder.cs");
            var methods = SplitIntoMethods(src);

            var sawTeardown = new List<string>();
            foreach (var kvp in methods)
            {
                if (!kvp.Value.Contains("loadedStates.Remove(")) continue;
                sawTeardown.Add(kvp.Key);

                if (CaptureSites.Contains(kvp.Key))
                {
                    Assert.True(kvp.Value.Contains("CaptureRailsSpanPartStates("),
                        $"'{kvp.Key}' is exempted as a capture site but no longer captures a snapshot");
                    continue;
                }

                Assert.True(kvp.Value.Contains("railsSpanPartStates.Remove("),
                    $"BackgroundRecorder.{kvp.Key} drops loadedStates but leaves railsSpanPartStates " +
                    "behind. persistentId is craft-baked, so the orphan is handed to the next launch " +
                    "of the same craft and diffed as if it were the same flight.");
            }

            // Canary: if the crude method split ever stops matching, the loop above would pass
            // vacuously.
            Assert.Contains("OnBackgroundVesselWillDestroy", sawTeardown);
            Assert.Contains("RetireDestroyedBackgroundEntry", sawTeardown);
            Assert.True(sawTeardown.Count >= 5,
                $"Expected at least 5 loadedStates teardown sites, saw {sawTeardown.Count}");
        }

        private static Dictionary<string, string> SplitIntoMethods(string src)
        {
            // Crude but sufficient: a class-member-indent line carrying an access modifier and an
            // opening paren starts a method; its body runs to the next such line.
            var header = new Regex(
                @"^\s{8}(?:public|private|internal|protected)[^=;]*?\b(\w+)\s*\(",
                RegexOptions.Compiled);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string current = null;
            var body = new StringBuilder();
            foreach (string line in src.Split('\n'))
            {
                Match m = header.Match(line);
                if (m.Success)
                {
                    if (current != null && !result.ContainsKey(current))
                        result[current] = body.ToString();
                    current = m.Groups[1].Value;
                    body.Length = 0;
                }
                body.Append(line).Append('\n');
            }
            if (current != null && !result.ContainsKey(current))
                result[current] = body.ToString();
            return result;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
