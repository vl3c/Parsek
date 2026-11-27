using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// PART-STATES-LOST-AT-PROMOTION: a promotion skips the full seed set (bug A / #263), so a part
    /// state that changed while no recorder watched was in no event and the ghost never took it
    /// up. The promotion now writes exactly the states where what the recording's ghost would show
    /// (its spawn pose plus the recorded events) and the live part differ.
    /// </summary>
    [Collection("Sequential")]
    public class PromotionPartStateTests : IDisposable
    {
        private const uint Pid = 4242u;
        private const uint OtherPid = 77u;
        private const double SeedUT = 100.0;
        private const double LastInterestingUT = 250.0;
        private const double NowUT = 1000.0;
        private const int ModuleIndex = 3;
        private readonly List<string> logLines = new List<string>();

        public PromotionPartStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ulong Key(int midx = ModuleIndex, uint pid = Pid)
            => FlightRecorder.EncodeEngineKey(pid, midx);

        private static PartEvent Ev(double ut, PartEventType type, int midx = 0, uint pid = Pid, float value = 0f)
            => new PartEvent
            {
                ut = ut, partPersistentId = pid, eventType = type, partName = "part",
                moduleIndex = midx, value = value
            };

        private static List<PartEvent> Reconcile(
            PartTrackingSets live, PromotionPartFacts fact, params PartEvent[] recorded)
        {
            var events = new List<PartEvent>();
            int emitted = PartStateSeeder.EmitPromotionStateEvents(
                live, new List<PromotionPartFacts> { fact }, recorded.ToList(), events,
                SeedUT, LastInterestingUT, NowUT, "Recorder");
            Assert.Equal(events.Count, emitted);
            return events;
        }

        /// <summary>One family: how the live set, the spawn pose and the events spell its two states.</summary>
        public sealed class Family
        {
            public string Name;
            public PartEventType On;
            public PartEventType? Off;
            public int Midx;
            internal Action<PartTrackingSets> LiveOn;
            /// <summary>Null when "off" is the absence from the live set.</summary>
            internal Action<PartTrackingSets> LiveOff;
            internal Action<PromotionPartFacts> Has;
            internal Action<PromotionPartFacts, bool> Spawn;
            public override string ToString() => Name;
        }

        private static readonly Family[] Families =
        {
            new Family { Name = "light", On = PartEventType.LightOn, Off = PartEventType.LightOff,
                LiveOn = s => s.lightsOn.Add(Pid), Has = f => f.hasSnapshotReadableLight = true,
                Spawn = (f, on) => f.spawn.lightOn = on },
            new Family { Name = "cabinLight", On = PartEventType.LightOn, Off = PartEventType.LightOff,
                LiveOn = s => s.lightsOn.Add(Pid), Has = f => f.hasSnapshotReadableLight = true,
                Spawn = (f, on) => f.spawn.colorChangerOn = on },
            new Family { Name = "blink", On = PartEventType.LightBlinkEnabled, Off = PartEventType.LightBlinkDisabled,
                LiveOn = s => { s.blinkingLights.Add(Pid); s.lightBlinkRates[Pid] = 2f; },
                Has = f => f.hasSnapshotReadableLight = true,
                Spawn = (f, on) => { f.spawn.lightBlinking = on; f.spawn.lightBlinkRate = 2f; } },
            new Family { Name = "shroud", On = PartEventType.ShroudJettisoned, Off = null,
                LiveOn = s => s.jettisonedShrouds.Add(Pid), Has = f => f.hasShroud = true,
                Spawn = (f, on) => f.spawnShroudJettisoned = on },
            new Family { Name = "fairing", On = PartEventType.FairingJettisoned, Off = null,
                LiveOn = s => s.deployedFairings.Add(Pid), Has = f => f.hasFairing = true,
                Spawn = (f, on) => f.spawnFairingDeployed = on },
            new Family { Name = "panel", On = PartEventType.DeployableExtended, Off = PartEventType.DeployableRetracted,
                LiveOn = s => s.extendedDeployables.Add(Pid), Has = f => f.hasDeployable = f.hasPosedDeployable = true,
                Spawn = (f, on) => f.spawn.deployableExtended = on },
            new Family { Name = "brokenPanel", On = PartEventType.DeployableBroken, Off = PartEventType.DeployableRetracted,
                LiveOn = s => s.brokenDeployables.Add(Pid), Has = f => f.hasDeployable = f.hasPosedDeployable = true,
                Spawn = (f, on) => { f.spawn.deployableBroken = on; if (!on) f.spawn.deployableExtended = false; } },
            new Family { Name = "brokenWheel", On = PartEventType.WheelBroken, Off = PartEventType.WheelRepaired,
                LiveOn = s => s.damagedWheelParts.Add(Pid), Has = f => f.hasWheelDamage = true,
                Spawn = (f, on) => f.spawn.wheelDamaged = on },
            new Family { Name = "gear", On = PartEventType.GearDeployed, Off = PartEventType.GearRetracted,
                LiveOn = s => s.deployedGear.Add(Pid), Has = f => f.hasGear = true,
                Spawn = (f, on) => f.spawn.gearDeployed = on },
            new Family { Name = "cargoBay", On = PartEventType.CargoBayOpened, Off = PartEventType.CargoBayClosed,
                LiveOn = s => s.openCargoBays.Add(Pid), Has = f => f.hasTrackedCargoBay = true,
                Spawn = (f, on) => f.spawn.cargoBayOpen = on },
            new Family { Name = "chute", On = PartEventType.ParachuteDeployed, Off = PartEventType.ParachuteCut,
                LiveOn = s => s.parachuteStates[Pid] = FlightRecorder.ParachuteStateDeployed,
                LiveOff = s => s.parachuteStates[Pid] = FlightRecorder.ParachuteStateCut,
                Has = f => f.hasChute = true,
                Spawn = (f, on) => f.spawn.parachutePersistentState = on ? "DEPLOYED" : "STOWED" },
            Module("ladder", PromotionModuleFamily.Ladder, s => s.deployedLadders,
                f => f.ladderIndex = ModuleIndex, (f, on) => f.spawn.ladderExtended = on),
            Module("animationGroup", PromotionModuleFamily.AnimationGroup, s => s.deployedAnimationGroups,
                f => f.animationGroupIndex = ModuleIndex, (f, on) => f.spawn.animationGroupDeployed = on),
            Module("animateGeneric", PromotionModuleFamily.AnimateGeneric, s => s.deployedAnimateGenericModules,
                f => f.animateGenericIndex = ModuleIndex, (f, on) => f.spawn.animateGenericDeployed = on),
            Module("aeroSurface", PromotionModuleFamily.AeroSurface, s => s.deployedAeroSurfaceModules,
                f => f.aeroSurfaceIndex = ModuleIndex, (f, on) => f.spawn.surfaceDeployed = on),
            Module("controlSurface", PromotionModuleFamily.ControlSurface, s => s.deployedControlSurfaceModules,
                f => f.controlSurfaceIndex = ModuleIndex, (f, on) => f.spawn.surfaceDeployed = on),
        };

        private static Family Module(
            string name, PromotionModuleFamily family, Func<PartTrackingSets, HashSet<ulong>> set,
            Action<PromotionPartFacts> index, Action<PromotionPartFacts, bool> spawn)
        {
            return new Family
            {
                Name = name, On = PartEventType.DeployableExtended, Off = PartEventType.DeployableRetracted,
                Midx = ModuleIndex, LiveOn = s => set(s).Add(Key()), Spawn = spawn,
                Has = f => { f.moduleFamilies[ModuleIndex] = family; index(f); },
            };
        }

        public static IEnumerable<object[]> FamilyNames() => Families.Select(f => new object[] { f.Name });

        private static Family Named(string name) => Families.Single(f => f.Name == name);

        /// <summary>A part of the family: no spawn pose known (null) or one read from the start snapshot.</summary>
        private static PromotionPartFacts Fact(Family family, bool? spawnOn)
        {
            var fact = new PromotionPartFacts { pid = Pid, partName = "part" };
            family.Has(fact);
            if (!spawnOn.HasValue) return fact;
            fact.spawn = new SnapshotPartBaseline();
            family.Spawn(fact, spawnOn.Value);
            return fact;
        }

        private static PartTrackingSets Live(Family family, bool on)
        {
            var sets = new PartTrackingSets();
            if (on) family.LiveOn(sets);
            else family.LiveOff?.Invoke(sets);
            return sets;
        }

        #region Per family

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void AFreshRecording_WritesNothing_ItsGhostSpawnsFromTheSnapshotTakenNow(string name)
        {
            Family family = Named(name);
            Assert.Empty(Reconcile(Live(family, true), Fact(family, null)));
            Assert.Empty(Reconcile(Live(family, false), Fact(family, null)));
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void TheStartSnapshotAlreadyShowsTheState_WritesNothing(string name)
        {
            Family family = Named(name);
            Assert.Empty(Reconcile(Live(family, true), Fact(family, true)));
            Assert.Empty(Reconcile(Live(family, false), Fact(family, false)));
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void SwitchedOnSinceTheStartSnapshot_WritesTheOnEvent(string name)
        {
            Family family = Named(name);
            PartEvent e = Assert.Single(Reconcile(Live(family, true), Fact(family, false)));
            Assert.Equal(family.On, e.eventType);
            Assert.Equal(Pid, e.partPersistentId);
            Assert.Equal(family.Midx, e.moduleIndex);
            Assert.Equal("part", e.partName);
            Assert.Equal(LastInterestingUT, e.ut);
            Assert.Contains(logLines, l => l.Contains("[Recorder]") && l.Contains(
                $"Seed event: {family.On} pid=4242 midx={family.Midx} part='part' ut=250.00 (promotion)"));
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void SwitchedOffSinceTheStartSnapshot_WritesTheOffEventOrNothing(string name)
        {
            Family family = Named(name);
            List<PartEvent> events = Reconcile(Live(family, false), Fact(family, true));
            if (family.Off.HasValue)
                Assert.Equal(family.Off.Value, Assert.Single(events).eventType);
            else
                Assert.Empty(events);
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void TheRecordedEventAlreadySaysIt_WritesNothing(string name)
        {
            Family family = Named(name);
            Assert.Empty(Reconcile(Live(family, true), Fact(family, false),
                Ev(200, family.On, family.Midx, value: 2f)));
            if (family.Off.HasValue)
                Assert.Empty(Reconcile(Live(family, false), Fact(family, true),
                    Ev(200, family.Off.Value, family.Midx)));
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void TheRecordedEventContradictsTheLivePart_WritesTheOtherDirection(string name)
        {
            Family family = Named(name);
            if (!family.Off.HasValue) return;
            foreach (bool? spawn in new bool?[] { null, false, true })
            {
                PartEvent off = Assert.Single(Reconcile(Live(family, false), Fact(family, spawn),
                    Ev(200, family.On, family.Midx, value: 2f)));
                Assert.Equal(family.Off.Value, off.eventType);
                Assert.Equal(family.Midx, off.moduleIndex);
                PartEvent on = Assert.Single(Reconcile(Live(family, true), Fact(family, spawn),
                    Ev(200, family.Off.Value, family.Midx)));
                Assert.Equal(family.On, on.eventType);
            }
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void APartTheStartSnapshotDoesNotHold_IsNotDrawn_WritesNothing(string name)
        {
            Family family = Named(name);
            PromotionPartFacts fact = Fact(family, null);
            fact.absentFromStartSnapshot = true;
            Assert.Empty(Reconcile(Live(family, true), fact));
            Assert.Empty(Reconcile(Live(family, false), fact));
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void APartThatLeftTheVessel_IsIgnored(string name)
        {
            Family family = Named(name);
            var stranger = new PromotionPartFacts { pid = OtherPid, partName = "other" };
            Assert.Empty(Reconcile(Live(family, false), stranger,
                Ev(200, family.On, family.Midx, value: 2f)));
        }

        #endregion

        #region The states the start snapshot does not carry

        [Theory]
        [InlineData(HeatLevel.Hot, PartEventType.ThermalAnimationHot)]
        [InlineData(HeatLevel.Medium, PartEventType.ThermalAnimationMedium)]
        public void Heat_IsInNoSnapshot_SoAFreshRecordingWritesIt(object level, PartEventType expected)
        {
            var live = new PartTrackingSets();
            live.animateHeatLevels[Key()] = (HeatLevel)level;
            var fact = new PromotionPartFacts { pid = Pid, partName = "part" };
            PartEvent e = Assert.Single(Reconcile(live, fact));
            Assert.Equal(expected, e.eventType);
            Assert.Equal(ModuleIndex, e.moduleIndex);

            fact.spawn = new SnapshotPartBaseline();
            Assert.Equal(expected, Assert.Single(Reconcile(live, fact)).eventType);
            Assert.Empty(Reconcile(live, fact, Ev(200, expected, ModuleIndex)));
        }

        [Fact]
        public void Heat_CoolNow_WritesNothing_OrColdWhenTheRecordingSaysHot()
        {
            var fact = new PromotionPartFacts { pid = Pid, partName = "part" };
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
            Assert.Equal(PartEventType.ThermalAnimationCold, Assert.Single(Reconcile(
                new PartTrackingSets(), fact,
                Ev(200, PartEventType.ThermalAnimationHot, ModuleIndex))).eventType);
            Assert.Empty(Reconcile(new PartTrackingSets(), fact,
                Ev(200, PartEventType.ThermalAnimationHot, ModuleIndex),
                Ev(210, PartEventType.ThermalAnimationCold, ModuleIndex)));
        }

        [Fact]
        public void Heat_OfAPartTheStartSnapshotDoesNotHold_IsComparedWithItsEventsOnly()
        {
            var live = new PartTrackingSets();
            live.animateHeatLevels[Key()] = HeatLevel.Hot;
            var fact = new PromotionPartFacts { pid = Pid, partName = "part", absentFromStartSnapshot = true };
            Assert.Empty(Reconcile(live, fact));
            Assert.Equal(PartEventType.ThermalAnimationHot, Assert.Single(Reconcile(live, fact,
                Ev(200, PartEventType.ThermalAnimationMedium, ModuleIndex))).eventType);
        }

        [Fact]
        public void Heat_OfAnotherPart_IsLeftAlone()
        {
            var live = new PartTrackingSets();
            live.animateHeatLevels[Key(pid: OtherPid)] = HeatLevel.Hot;
            Assert.Empty(Reconcile(live, new PromotionPartFacts { pid = Pid, partName = "part" }));
        }

        [Theory]
        [InlineData(FlightRecorder.ParachuteStateSemiDeployed, PartEventType.ParachuteSemiDeployed)]
        [InlineData(FlightRecorder.ParachuteStateDeployed, PartEventType.ParachuteDeployed)]
        public void AnEvaChute_IsNotReadFromTheSnapshot_SoAFreshRecordingWritesIt(
            int state, PartEventType expected)
        {
            var live = new PartTrackingSets();
            live.parachuteStates[Pid] = state;
            var fact = new PromotionPartFacts
            {
                pid = Pid, partName = "kerbalEVA", hasChute = true, chuteOutsideSnapshotReader = true
            };
            Assert.Equal(expected, Assert.Single(Reconcile(live, fact)).eventType);
            fact.spawn = new SnapshotPartBaseline();
            Assert.Equal(expected, Assert.Single(Reconcile(live, fact)).eventType);
            Assert.Empty(Reconcile(live, fact, Ev(200, expected)));
        }

        [Fact]
        public void AnEvaChute_StowedOrAlreadyCut_WritesNothing_AndCutAfterARecordedCanopyWritesCut()
        {
            var fact = new PromotionPartFacts
            {
                pid = Pid, partName = "kerbalEVA", hasChute = true, chuteOutsideSnapshotReader = true
            };
            var cut = new PartTrackingSets();
            cut.parachuteStates[Pid] = FlightRecorder.ParachuteStateCut;
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
            Assert.Empty(Reconcile(cut, fact));
            Assert.Equal(PartEventType.ParachuteCut, Assert.Single(Reconcile(
                cut, fact, Ev(200, PartEventType.ParachuteDeployed))).eventType);
        }

        [Fact]
        public void ARobotArmScanner_IsNotReadFromTheSnapshot_SoAFreshRecordingWritesIt()
        {
            var fact = new PromotionPartFacts
            {
                pid = Pid, partName = "arm", hasDeployable = true, hasPosedDeployable = true,
                deployableOutsideSnapshotReader = true, spawn = new SnapshotPartBaseline()
            };
            fact.moduleFamilies[ModuleIndex] = PromotionModuleFamily.RobotArmScanner;
            var live = new PartTrackingSets();
            live.extendedDeployables.Add(Pid);
            live.deployedRobotArmScannerModules.Add(Key());
            // The same pair an ordinary recording start seeds for it.
            Assert.Equal(new[] { 0, ModuleIndex }, Reconcile(live, fact).Select(e => e.moduleIndex).OrderBy(m => m));
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
            var broken = new PartTrackingSets();
            broken.brokenDeployables.Add(Pid);
            Assert.Equal(PartEventType.DeployableBroken, Assert.Single(Reconcile(broken, fact)).eventType);
        }

        #endregion

        #region Parachute and panel transitions

        [Theory]
        [InlineData("STOWED", FlightRecorder.ParachuteStateSemiDeployed, PartEventType.ParachuteSemiDeployed)]
        [InlineData("ACTIVE", FlightRecorder.ParachuteStateDeployed, PartEventType.ParachuteDeployed)]
        [InlineData("SEMIDEPLOYED", FlightRecorder.ParachuteStateDeployed, PartEventType.ParachuteDeployed)]
        [InlineData("DEPLOYED", FlightRecorder.ParachuteStateCut, PartEventType.ParachuteCut)]
        [InlineData("CUT", FlightRecorder.ParachuteStateStowed, PartEventType.ParachuteRepacked)]
        public void Chute_EveryStepSinceTheSnapshotIsWritten(string spawn, int live, PartEventType expected)
        {
            var sets = new PartTrackingSets();
            if (live != FlightRecorder.ParachuteStateStowed) sets.parachuteStates[Pid] = live;
            var fact = Fact(Named("chute"), true);
            fact.spawn.parachutePersistentState = spawn;
            Assert.Equal(expected, Assert.Single(Reconcile(sets, fact)).eventType);
        }

        [Fact]
        public void Panel_RecordedExtendedAndBrokenNow_WritesBrokenOnly()
        {
            var live = new PartTrackingSets();
            live.brokenDeployables.Add(Pid);
            Assert.Equal(PartEventType.DeployableBroken, Assert.Single(Reconcile(
                live, Fact(Named("panel"), false), Ev(200, PartEventType.DeployableExtended))).eventType);
        }

        [Fact]
        public void Panel_RecordedBrokenThenRepairedAndExtended_WritesExtended()
        {
            // Repaired and re-deployed: the two events the rails-span diff writes for it too.
            var live = new PartTrackingSets();
            live.extendedDeployables.Add(Pid);
            Assert.Equal(
                new[] { PartEventType.DeployableRetracted, PartEventType.DeployableExtended },
                Reconcile(live, Fact(Named("panel"), false), Ev(200, PartEventType.DeployableBroken))
                    .Select(e => e.eventType).ToArray());
        }

        [Fact]
        public void Panel_WithNoPoseAnimation_NeverWritesExtendedOrRetracted()
        {
            // An OX-STAT: the snapshot says EXTENDED forever and the seeder never tracks it.
            var fact = Fact(Named("panel"), true);
            fact.hasPosedDeployable = false;
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
            var broken = new PartTrackingSets();
            broken.brokenDeployables.Add(Pid);
            Assert.Equal(PartEventType.DeployableBroken, Assert.Single(Reconcile(broken, fact)).eventType);
        }

        [Fact]
        public void Panel_RecordedExtendedThenBroken_RepairedToRetractedNow_WritesOneRetracted()
        {
            Assert.Equal(PartEventType.DeployableRetracted, Assert.Single(Reconcile(
                new PartTrackingSets(), Fact(Named("panel"), false),
                Ev(200, PartEventType.DeployableExtended),
                Ev(210, PartEventType.DeployableBroken))).eventType);
        }

        [Fact]
        public void Chute_RecordedCutAndRepacked_StowedNow_WritesNothing()
        {
            Assert.Empty(Reconcile(new PartTrackingSets(), Fact(Named("chute"), false),
                Ev(200, PartEventType.ParachuteDeployed),
                Ev(210, PartEventType.ParachuteCut),
                Ev(220, PartEventType.ParachuteRepacked)));
        }

        [Fact]
        public void Panel_SnapshotBrokenAndIntactNow_WritesTheRepair()
        {
            Assert.Equal(PartEventType.DeployableRetracted, Assert.Single(Reconcile(
                new PartTrackingSets(), Fact(Named("brokenPanel"), true))).eventType);
        }

        [Fact]
        public void Deployable_MidTravelInTheSnapshot_IsNotGuessed()
        {
            var fact = Fact(Named("panel"), true);
            fact.spawn.deployableExtended = null;
            Assert.Empty(Reconcile(Live(Named("panel"), true), fact));
            Assert.Empty(Reconcile(Live(Named("panel"), false), fact));
        }

        [Fact]
        public void ModuleFamily_UnclassifiedNow_IsNotComparedWithTheSnapshot()
        {
            var fact = Fact(Named("animationGroup"), true);
            fact.animationGroupIndex = -1;
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
        }

        [Fact]
        public void APartWithAPanelAndALadder_ReadsEachEventBackIntoItsOwnFamily()
        {
            var fact = Fact(Named("ladder"), false);
            fact.hasDeployable = fact.hasPosedDeployable = true;
            fact.spawn.deployableExtended = false;
            // The panel is recorded extended (index 0) and the ladder extended (its own index);
            // only the ladder is still out.
            List<PartEvent> events = Reconcile(Live(Named("ladder"), true), fact,
                Ev(200, PartEventType.DeployableExtended, 0),
                Ev(201, PartEventType.DeployableExtended, ModuleIndex));
            PartEvent e = Assert.Single(events);
            Assert.Equal(PartEventType.DeployableRetracted, e.eventType);
            Assert.Equal(0, e.moduleIndex);
        }

        [Fact]
        public void ALadderAtModuleIndexZero_IsNotReadAsAPanel()
        {
            var fact = new PromotionPartFacts { pid = Pid, partName = "part" };
            fact.moduleFamilies[0] = PromotionModuleFamily.Ladder;
            var live = new PartTrackingSets();
            live.deployedLadders.Add(Key(0));
            Assert.Empty(Reconcile(live, fact, Ev(200, PartEventType.DeployableExtended, 0)));
            Assert.Equal(PartEventType.DeployableRetracted, Assert.Single(Reconcile(
                new PartTrackingSets(), fact, Ev(200, PartEventType.DeployableExtended, 0))).eventType);
        }

        [Fact]
        public void Blink_RateChanged_WritesTheNewRate()
        {
            var live = new PartTrackingSets();
            live.blinkingLights.Add(Pid);
            live.lightBlinkRates[Pid] = 5f;
            PartEvent e = Assert.Single(Reconcile(live, Fact(Named("blink"), true)));
            Assert.Equal(PartEventType.LightBlinkRate, e.eventType);
            Assert.Equal(5f, e.value);
            Assert.Empty(Reconcile(live, Fact(Named("blink"), true),
                Ev(200, PartEventType.LightBlinkRate, value: 5f)));
        }

        [Fact]
        public void ALampTheSnapshotReaderReadsDifferently_IsComparedWithItsEventsOnly()
        {
            var fact = Fact(Named("light"), true);
            fact.hasSnapshotReadableLight = false;
            Assert.Empty(Reconcile(new PartTrackingSets(), fact));
            Assert.Equal(PartEventType.LightOff, Assert.Single(Reconcile(
                new PartTrackingSets(), fact, Ev(200, PartEventType.LightOn))).eventType);
        }

        #endregion

        #region Last word, UT, and what stays out

        [Fact]
        public void TheLastWordIsReadByUT_NotByListOrder_AndATieGoesToTheLaterEntry()
        {
            Family light = Named("light");
            Assert.Empty(Reconcile(Live(light, true), Fact(light, false),
                Ev(210, PartEventType.LightOn), Ev(200, PartEventType.LightOff)));
            Assert.Empty(Reconcile(Live(light, true), Fact(light, false),
                Ev(200, PartEventType.LightOff), Ev(200, PartEventType.LightOn)));
            Assert.Single(Reconcile(Live(light, true), Fact(light, false),
                Ev(200, PartEventType.LightOn), Ev(200, PartEventType.LightOff)));
        }

        [Fact]
        public void AnotherPartsEvents_DoNotCount()
        {
            Family light = Named("light");
            Assert.Single(Reconcile(Live(light, true), Fact(light, false),
                Ev(200, PartEventType.LightOn, pid: OtherPid)));
        }

        [Fact]
        public void EnginesRcsConvertersAndTheKerbal_AreNotTouched()
        {
            var live = new PartTrackingSets();
            live.activeEngineKeys.Add(Key());
            live.lastThrottle[Key()] = 1f;
            live.allEngineKeys.Add(Key());
            live.activeRcsKeys.Add(Key());
            live.lastRcsThrottle[Key()] = 1f;
            live.activeConverterParts.Add(Pid);
            live.jetpackDeployedParts.Add(Pid);
            live.ragdollParts.Add(Pid);
            var fact = new PromotionPartFacts { pid = Pid, partName = "part", spawn = new SnapshotPartBaseline() };
            Assert.Empty(Reconcile(live, fact));
            Assert.Empty(Reconcile(new PartTrackingSets(), fact,
                Ev(200, PartEventType.EngineIgnited, ModuleIndex, value: 1f),
                Ev(200, PartEventType.RCSActivated, ModuleIndex, value: 1f),
                Ev(200, PartEventType.ConverterActivated),
                Ev(200, PartEventType.EvaJetpackDeployed),
                Ev(200, PartEventType.EvaRagdollStarted)));
        }

        [Fact]
        public void TheLiveSetsAreNotChanged()
        {
            PartTrackingSets live = Live(Named("light"), true);
            live.animateHeatLevels[Key()] = HeatLevel.Hot;
            Reconcile(live, Fact(Named("light"), false), Ev(200, PartEventType.LightOff));
            Assert.Contains(Pid, live.lightsOn);
            Assert.Equal(HeatLevel.Hot, live.animateHeatLevels[Key()]);
        }

        [Theory]
        // A fresh recording: its start.
        [InlineData(double.NaN, double.NaN, SeedUT)]
        // The recording's last interesting moment, at or after the part's own last event.
        [InlineData(double.NaN, 250.0, 250.0)]
        [InlineData(200.0, 250.0, 250.0)]
        [InlineData(250.0, 250.0, 250.0)]
        [InlineData(260.0, 250.0, 260.0)]
        // The recording is interesting up to the promotion: never inside the visit that starts now.
        [InlineData(200.0, NowUT, 200.0)]
        [InlineData(200.0, NowUT + 1, 200.0)]
        [InlineData(double.NaN, NowUT, SeedUT)]
        [InlineData(200.0, double.NaN, 200.0)]
        public void ResolvePromotionStateUT_Table(double lastPartEvent, double lastInteresting, double expected)
        {
            Assert.Equal(expected, PartStateSeeder.ResolvePromotionStateUT(
                SeedUT, lastPartEvent, lastInteresting, NowUT));
        }

        [Fact]
        public void TheUTIsThePartsOwnLastEventOfTheseFamilies_WhenTheRecordingIsInterestingUpToNow()
        {
            var events = new List<PartEvent>();
            Family light = Named("light");
            PartStateSeeder.EmitPromotionStateEvents(
                Live(light, true), new List<PromotionPartFacts> { Fact(light, false) },
                new List<PartEvent>
                {
                    Ev(200, PartEventType.LightOff),
                    Ev(300, PartEventType.EngineIgnited, value: 1f),
                    Ev(400, PartEventType.LightOff, pid: OtherPid),
                },
                events, SeedUT, NowUT, NowUT, "Recorder");
            Assert.Equal(200, Assert.Single(events).ut);
        }

        [Fact]
        public void NullAndEmptyInputs_WriteNothing()
        {
            var events = new List<PartEvent>();
            Family light = Named("light");
            var facts = new List<PromotionPartFacts> { Fact(light, false) };
            Assert.Equal(0, PartStateSeeder.EmitPromotionStateEvents(
                null, facts, null, events, 1, 1, 1, "Recorder"));
            Assert.Equal(0, PartStateSeeder.EmitPromotionStateEvents(
                Live(light, true), null, null, events, 1, 1, 1, "Recorder"));
            Assert.Empty(events);
            // No events recorded at all still compares with the spawn pose.
            Assert.Equal(1, PartStateSeeder.EmitPromotionStateEvents(
                Live(light, true), facts, null, events, 1, double.NaN, 5, "Recorder"));
        }

        #endregion

        #region The tail trim and the do-nothing visit

        private static Recording Committed()
        {
            var rec = new Recording { RecordingId = "resumed" };
            foreach (double ut in new[] { 100.0, 250.0, 600.0, NowUT, 1500.0 })
                rec.Points.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            rec.PartEvents.Add(Ev(200, PartEventType.GearRetracted));
            rec.PartEvents.Add(Ev(250, PartEventType.LightOn, pid: OtherPid));
            return rec;
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void ADoNothingResumeOfAChangedVessel_IsStillANoOp_AndTheTrimDoesNotMove(string name)
        {
            Family family = Named(name);
            Recording rec = Committed();
            double before = RecordingOptimizer.FindLastInterestingUT(rec);
            Assert.Equal(250.0, before);

            var events = new List<PartEvent>();
            PartStateSeeder.EmitPromotionStateEvents(
                Live(family, true), new List<PromotionPartFacts> { Fact(family, false) },
                rec.PartEvents, events, rec.StartUT, before, NowUT, "Recorder");
            Assert.Single(events);
            rec.PartEvents.AddRange(events);

            Assert.Equal(before, RecordingOptimizer.FindLastInterestingUT(rec));
            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, NowUT, out string reason));
            Assert.Null(reason);
        }

        [Theory]
        [MemberData(nameof(FamilyNames))]
        public void ARefusedCopyOfARecordingInterestingToItsEnd_IsStillANoOp(string name)
        {
            // The committed recording drove up to its last point, so the state event is stamped
            // exactly at its end, the anchor RefusedResumedCopyTail reads the copy's tail from.
            Family family = Named(name);
            Recording Committed100()
            {
                var rec = new Recording { RecordingId = "r" };
                foreach (double ut in new[] { 0.0, 50.0, 100.0 })
                    rec.Points.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceMobile,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 0, endUT = 100,
                    frames = new List<TrajectoryPoint>(), checkpoints = new List<OrbitSegment>(),
                });
                return rec;
            }
            RecordingTree Tree(Recording rec)
            {
                var tree = new RecordingTree { Id = "tree", TreeName = "t", RootRecordingId = "r" };
                tree.Recordings["r"] = rec;
                return tree;
            }

            Recording committed = Committed100();
            Recording copy = Committed100();
            foreach (double ut in new[] { 500.0, 501.0, 503.0 })
                copy.Points.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            double lastInteresting = RecordingOptimizer.FindLastInterestingUT(committed);
            Assert.Equal(committed.EndUT, lastInteresting);
            var events = new List<PartEvent>();
            PartStateSeeder.EmitPromotionStateEvents(
                Live(family, true), new List<PromotionPartFacts> { Fact(family, false) },
                committed.PartEvents, events, committed.StartUT, lastInteresting, 500, "Recorder");
            Assert.Equal(100, Assert.Single(events).ut);
            copy.PartEvents.AddRange(events);

            RefusedResumedCopyTail.Result r = RefusedResumedCopyTail.Evaluate(Tree(copy), Tree(committed));
            Assert.Null(r.KeepReason);
            Assert.True(r.IsNoOp);

            // A change the resumed recorder polled, and any other event at the anchor, still count.
            copy.PartEvents.Add(Ev(502, PartEventType.GearDeployed));
            Assert.Equal("meaningful-tail:r:part-event:GearDeployed",
                RefusedResumedCopyTail.Evaluate(Tree(copy), Tree(committed)).KeepReason);
            copy.PartEvents[copy.PartEvents.Count - 1] = Ev(100, PartEventType.Decoupled);
            Assert.Equal("meaningful-tail:r:part-event:Decoupled",
                RefusedResumedCopyTail.Evaluate(Tree(copy), Tree(committed)).KeepReason);
        }

        [Fact]
        public void AFreshSegmentOntoAKerbalUnderCanopy_IsStillANoOpVisit_AndALaterDeployIsThePlayers()
        {
            Recording Segment(double ut)
            {
                var rec = new Recording { RecordingId = "seg", ExplicitStartUT = 100.0 };
                rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
                rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin" });
                rec.PartEvents.Add(Ev(ut, PartEventType.ParachuteDeployed));
                return rec;
            }

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(Segment(100.0), false, out string reason));
            Assert.Null(reason);
            Assert.Contains(logLines, l => l.Contains("[SwitchSegment]") && l.Contains("stateSeeds=1"));
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(Segment(100.5), false, out reason));
            Assert.Equal("part-event:ParachuteDeployed", reason);
            // Without an owner there is no start to compare with: kept.
            Assert.True(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(
                Ev(100.0, PartEventType.ParachuteDeployed)));
        }

        [Theory]
        [InlineData(PartEventType.Decoupled)]
        [InlineData(PartEventType.Destroyed)]
        [InlineData(PartEventType.Docked)]
        [InlineData(PartEventType.ParachuteDestroyed)]
        [InlineData(PartEventType.ConverterActivated)]
        public void AStructuralEventAtTheSegmentStart_IsStillMeaningful(PartEventType type)
        {
            Assert.False(PartStateSeeder.IsPromotionStateEvent(type));
            var rec = new Recording { RecordingId = "seg", ExplicitStartUT = 100.0 };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin" });
            rec.PartEvents.Add(Ev(100.0, type));
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("part-event:" + type, reason);
        }

        [Fact]
        public void AFreshSegment_SeedsAtItsStart()
        {
            const double switchUT = 900.0;
            var segment = new Recording { RecordingId = "segment", ExplicitStartUT = switchUT };
            segment.Points.Add(new TrajectoryPoint { ut = switchUT, bodyName = "Kerbin" });
            var live = new PartTrackingSets();
            live.animateHeatLevels[Key()] = HeatLevel.Hot;
            var events = new List<PartEvent>();
            PartStateSeeder.EmitPromotionStateEvents(
                live, new List<PromotionPartFacts> { new PromotionPartFacts { pid = Pid, partName = "part" } },
                segment.PartEvents, events,
                FlightRecorder.ResolveChainPromotionSeedUT(segment, switchUT),
                RecordingOptimizer.FindLastInterestingUT(segment), switchUT, "Recorder");
            Assert.Equal(switchUT, Assert.Single(events).ut);
        }

        [Fact]
        public void TheBackgroundRecordersOwnEvents_AreNotWrittenAgain()
        {
            // The same Layer-1 transitions BackgroundRecorder appends to a tracked member, up to
            // and including the promotion's own tick.
            var recorded = new[]
            {
                FlightRecorder.CheckParachuteTransition(Pid, "part",
                    FlightRecorder.ParachuteStateDeployed, new Dictionary<uint, int>(), NowUT).Value,
                FlightRecorder.CheckLightTransition(Pid, "part", true, new HashSet<uint>(), NowUT).Value,
                FlightRecorder.CheckJettisonTransition(Pid, "part", true, new HashSet<uint>(), NowUT).Value,
                FlightRecorder.CheckWheelDamageTransition(Pid, "part", true, new HashSet<uint>(), NowUT).Value,
            };
            var live = new PartTrackingSets();
            live.parachuteStates[Pid] = FlightRecorder.ParachuteStateDeployed;
            live.lightsOn.Add(Pid);
            live.jettisonedShrouds.Add(Pid);
            live.damagedWheelParts.Add(Pid);
            var fact = new PromotionPartFacts
            {
                pid = Pid, partName = "part", spawn = new SnapshotPartBaseline { lightOn = false },
                hasChute = true, hasShroud = true, hasWheelDamage = true, hasSnapshotReadableLight = true,
            };
            fact.spawn.parachutePersistentState = "STOWED";
            Assert.Empty(Reconcile(live, fact, recorded));
            Assert.Equal(4, Reconcile(live, fact).Count);
        }

        #endregion

        #region Reading the start snapshot

        private static ConfigNode PartNode(uint pid, params ConfigNode[] modules)
        {
            var node = new ConfigNode("PART");
            node.AddValue("persistentId", pid);
            foreach (ConfigNode module in modules) node.AddNode(module);
            return node;
        }

        private static ConfigNode ModuleNode(string name, params string[] keyValues)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", name);
            for (int i = 0; i + 1 < keyValues.Length; i += 2) node.AddValue(keyValues[i], keyValues[i + 1]);
            return node;
        }

        [Theory]
        [InlineData("isJettisoned", "True", true)]
        [InlineData("isJettisoned", "False", false)]
        [InlineData("shroudHideOverride", "True", true)]
        [InlineData("shroudHideOverride", "False", false)]
        [InlineData("isJettisoned", "junk", false)]
        public void ReadSnapshotShroud(string key, string value, bool expected)
        {
            PartStateSeeder.ReadSnapshotShroudAndFairing(
                PartNode(Pid, ModuleNode("ModuleJettison", key, value)), out bool shroud, out bool fairing);
            Assert.Equal(expected, shroud);
            Assert.False(fairing);
        }

        [Theory]
        [InlineData("st_flight_deployed", true)]
        [InlineData("st_idle", false)]
        [InlineData(null, false)]
        public void ReadSnapshotFairing(string fsm, bool expected)
        {
            PartStateSeeder.ReadSnapshotShroudAndFairing(
                PartNode(Pid, fsm == null
                    ? ModuleNode("ModuleProceduralFairing")
                    : ModuleNode("ModuleProceduralFairing", "fsm", fsm)),
                out bool shroud, out bool fairing);
            Assert.Equal(expected, fairing);
            Assert.False(shroud);
        }

        [Fact]
        public void ReadSnapshotShroudAndFairing_AnyJettisonedModuleCounts_AndOtherModulesDoNot()
        {
            PartStateSeeder.ReadSnapshotShroudAndFairing(PartNode(Pid,
                ModuleNode("ModuleJettison", "isJettisoned", "True"),
                ModuleNode("ModuleJettison", "isJettisoned", "False"),
                ModuleNode("ModuleLight", "fsm", "st_flight_deployed")), out bool shroud, out bool fairing);
            Assert.True(shroud);
            Assert.False(fairing);
            PartStateSeeder.ReadSnapshotShroudAndFairing(null, out shroud, out fairing);
            Assert.False(shroud || fairing);
        }

        [Fact]
        public void IndexSnapshotParts_KeysByPid_SkippingZeroDuplicatesAndJunk()
        {
            var snapshot = new ConfigNode("VESSEL");
            ConfigNode first = PartNode(5u);
            snapshot.AddNode(first);
            snapshot.AddNode(PartNode(5u));
            snapshot.AddNode(PartNode(0u));
            snapshot.AddNode(new ConfigNode("PART"));
            Dictionary<uint, ConfigNode> nodes = PartStateSeeder.IndexSnapshotParts(snapshot);
            Assert.Single(nodes);
            Assert.Same(first, nodes[5u]);
            Assert.Empty(PartStateSeeder.IndexSnapshotParts(null));
        }

        [Fact]
        public void TheReaderAndTheGhostBuilderReadOneSnapshotTheSameWay()
        {
            ConfigNode node = PartNode(Pid, ModuleNode("ModuleDeployableSolarPanel", "deployState", "EXTENDED"),
                ModuleNode("ModuleEvaChute", "persistentState", "DEPLOYED"));
            Assert.True(PartStateSeeder.TryParseSnapshotPartBaseline(node).deployableExtended);
            Assert.True(GhostVisualBuilder.TryParseSnapshotPartBaseline(node).deployableExtended);
            // Why an EVA chute is reconciled against "off": the reader has no opinion on it.
            Assert.Null(PartStateSeeder.TryParseSnapshotPartBaseline(node).parachutePersistentState);
        }

        #endregion

        [Fact]
        public void ResetPartEventTrackingState_ReconcilesOnThePromotionBranchOnly_BeforeTheStartSnapshot()
        {
            string dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "Recording"));
            string text = File.ReadAllText(Path.Combine(dir, "FlightRecorder.PartEvents.cs")).Replace("\r\n", "\n");
            int branch = text.IndexOf("if (!emitSeedEvents)\n", StringComparison.Ordinal);
            int call = text.IndexOf(
                "EmitPartStateEventsForPromotion(v, activeRec, activeRecId);\n                return;",
                StringComparison.Ordinal);
            int full = text.IndexOf("PartStateSeeder.EmitSeedEvents(", StringComparison.Ordinal);
            Assert.True(branch >= 0 && call > branch && full > call,
                "the part state reconcile must be the last step of the promotion branch");

            string start = File.ReadAllText(Path.Combine(dir, "FlightRecorder.StartRecording.cs"));
            int reset = start.IndexOf("ResetPartEventTrackingState(v, emitSeedEvents: !isPromotion);", StringComparison.Ordinal);
            int prepare = start.IndexOf("PrepareQuickloadResumeStateIfNeeded();", StringComparison.Ordinal);
            int capture = start.IndexOf("CaptureStartSnapshots(v);", StringComparison.Ordinal);
            Assert.True(prepare >= 0 && reset > prepare && capture > reset,
                "a missing ghost snapshot must still mean 'captured later in this start', and the " +
                "recorded events must already be trimmed to the resume");
        }
    }
}
