using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS. The contract patch rebuilds a committed
    /// accept from its snapshot with <c>Contract.Load</c>, which never runs the template's
    /// <c>OnAccepted</c> (decompiled KSP 1.12.5): a RecoverAsset's stranded vessel and a
    /// TourismContract's tourist conversion are not rebuilt. These pin the pure classification,
    /// the guard a regeneration must pass and the repair the patch then makes.
    /// </summary>
    public class ContractAcceptWorldEffectsTests
    {
        // The post-accept shape of cs-career-mun4's RecoverAsset e8ba5ea6 (Rayvan Kerman in a
        // mk1pod in low Kerbin orbit). Stock's GenerateKerbalInPart sets partID on accept;
        // the snapshot is taken at onAccepted, after OnAccepted.
        private static ConfigNode RescueSnapshot(string recoveryType = "1", string partId = "4127703291",
            string recoveryLocation = "1")
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("guid", "e8ba5ea6-570c-4b7f-99b4-44c9dee51030");
            n.AddValue("type", "RecoverAsset");
            n.AddValue("state", "Active");
            n.AddValue("targetBody", "1");
            n.AddValue("kerbalName", "Rayvan Kerman");
            n.AddValue("partName", "mk1pod.v2");
            n.AddValue("partID", partId);
            if (recoveryLocation != null) n.AddValue("recoveryLocation", recoveryLocation);
            if (recoveryType != null) n.AddValue("recoveryType", recoveryType);
            var p = n.AddNode("PARAM");
            p.AddValue("name", "AcquireCrew");
            p.AddValue("kerbals", "Rayvan Kerman");
            return n;
        }

        // The rewind save's world: the offer created Rayvan (Unowned, in the roster), the
        // accept that spawned his pod lies after the save, so no vessel holds him.
        private static StrandedAssetWorldFacts RewindSaveWorld(
            bool committedResolution = false, bool reserved = false,
            bool inRoster = true, bool aboard = false, bool partInWorld = false,
            bool unowned = true)
        {
            return new StrandedAssetWorldFacts
            {
                PartInAnyVessel = id => partInWorld,
                KerbalAboardAnyVessel = name => aboard && name == "Rayvan Kerman",
                KerbalInRoster = name => inRoster && name == "Rayvan Kerman",
                KerbalUnowned = name => unowned && inRoster && name == "Rayvan Kerman",
                KerbalReserved = name => reserved,
                CommittedResolution = committedResolution
            };
        }

        private static ContractAcceptWorldEffectPlan PlanRescue(
            StrandedAssetWorldFacts world, GameScenes scene = GameScenes.SPACECENTER,
            ConfigNode snapshot = null)
        {
            return ContractAcceptWorldEffects.Plan(
                "RecoverAsset", snapshot ?? RescueSnapshot(), world, null, scene);
        }

        private static ConfigNode TourismSnapshot(string tourists)
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("type", "TourismContract");
            if (tourists != null)
                n.AddValue("tourists", tourists);
            return n;
        }

        // The rewind save's roster for a tourism offer that predates it: each tourist as the
        // offer made him, Unowned and Assigned.
        private static TouristRosterFact OfferedTourist()
        {
            return new TouristRosterFact { InRoster = true, IsUnowned = true };
        }

        [Fact]
        public void RebuiltRescue_InTheRewindSaveWorld_IsMissingItsStrandedVessel()
        {
            ContractAcceptWorldEffectPlan plan = PlanRescue(RewindSaveWorld());

            Assert.Equal(ContractAcceptWorldEffect.StrandedAsset, plan.Effect);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal("Regenerate", plan.Outcome);
            Assert.Contains("kerbal='Rayvan Kerman'", plan.Detail);
        }

        [Fact]
        public void RebuiltRescue_InTheRewindSaveWorld_AtTheSpaceCenter_RegeneratesTheStrandedVessel()
        {
            ContractAcceptWorldEffectPlan plan = PlanRescue(RewindSaveWorld());

            Assert.Equal(ContractAcceptRepair.RegenerateStrandedAsset, plan.Repair);
            Assert.Null(plan.RepairSkipReason);
            Assert.True(plan.Target.RecoveringKerbal);
            Assert.Equal("Rayvan Kerman", plan.Target.KerbalName);

            Assert.Equal(ContractAcceptRepair.RegenerateStrandedAsset,
                PlanRescue(RewindSaveWorld(), GameScenes.TRACKSTATION).Repair);
            // A part recovery needs no kerbal at all.
            Assert.Equal(ContractAcceptRepair.RegenerateStrandedAsset,
                PlanRescue(RewindSaveWorld(inRoster: false), snapshot: RescueSnapshot(recoveryType: "2")).Repair);
        }

        [Theory]
        [InlineData(GameScenes.FLIGHT)]
        [InlineData(GameScenes.EDITOR)]
        [InlineData(GameScenes.MAINMENU)]
        public void RebuiltRescue_OutsideSpaceCenterAndTrackingStation_IsDeferredToTheNextVisit(GameScenes scene)
        {
            // Owner ruling 2026-11-20: the stranded vessel comes at the next Space Center or
            // Tracking Station visit; the gap is no longer permanent.
            Assert.False(ContractAcceptWorldEffects.IsRegenerationScene(scene));
            ContractAcceptWorldEffectPlan plan = PlanRescue(RewindSaveWorld(), scene);
            Assert.Equal(ContractAcceptRepair.DeferStrandedAsset, plan.Repair);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal("Regenerate", plan.Outcome);
            Assert.Equal("scene=" + scene, plan.RepairSkipReason);
        }

        [Fact]
        public void RebuiltRescue_HeldByAGuard_OutsideTheRegenerationScenes_IsNotDeferred()
        {
            // Only a Regenerate decision waits for a regeneration scene; a guard is final.
            ContractAcceptWorldEffectPlan held = PlanRescue(
                RewindSaveWorld(committedResolution: true), GameScenes.FLIGHT);
            Assert.Equal(ContractAcceptRepair.None, held.Repair);
            Assert.Equal("CommittedResolution", held.RepairSkipReason);

            ContractAcceptWorldEffectPlan present = PlanRescue(
                RewindSaveWorld(aboard: true), GameScenes.FLIGHT);
            Assert.Equal(ContractAcceptRepair.None, present.Repair);
            Assert.Null(present.RepairSkipReason);
        }

        [Fact]
        public void RebuiltRescue_EveryGuard_LeavesTheWorldAlone()
        {
            // Already in the world (the rewind save holds the vessel, or a regeneration ran):
            // nothing missing, nothing to say.
            ContractAcceptWorldEffectPlan present = PlanRescue(RewindSaveWorld(aboard: true));
            Assert.Equal(ContractAcceptRepair.None, present.Repair);
            Assert.Null(present.RepairSkipReason);

            Assert.Equal(ContractAcceptRepair.None, PlanRescue(
                RewindSaveWorld(), snapshot: RescueSnapshot(recoveryType: null)).Repair);

            var held = new[]
            {
                new KeyValuePair<StrandedAssetWorldFacts, string>(
                    RewindSaveWorld(committedResolution: true), "CommittedResolution"),
                new KeyValuePair<StrandedAssetWorldFacts, string>(
                    RewindSaveWorld(reserved: true), "KerbalReserved"),
                new KeyValuePair<StrandedAssetWorldFacts, string>(
                    RewindSaveWorld(inRoster: false), "KerbalNotInRoster"),
                new KeyValuePair<StrandedAssetWorldFacts, string>(
                    RewindSaveWorld(unowned: false), "KerbalNotUnowned"),
            };
            foreach (var h in held)
            {
                ContractAcceptWorldEffectPlan plan = PlanRescue(h.Key);
                Assert.Equal(ContractAcceptRepair.None, plan.Repair);
                Assert.True(plan.WorldEffectMissing);
                Assert.Equal(h.Value, plan.Outcome);
                Assert.Equal(h.Value, plan.RepairSkipReason);
            }
        }

        [Fact]
        public void DecideStrandedAsset_KerbalNoLongerUnowned_IsNotSeatedAgain()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.KerbalNotUnowned,
                ContractAcceptWorldEffects.DecideStrandedAsset(t, RewindSaveWorld(unowned: false)));
            // No answer is no proof he is still the stranded kerbal.
            var noAnswer = RewindSaveWorld();
            noAnswer.KerbalUnowned = null;
            Assert.Equal(StrandedAssetDecision.KerbalNotUnowned,
                ContractAcceptWorldEffects.DecideStrandedAsset(t, noAnswer));
            // A part-only recovery never asks.
            RecoverAssetTarget part = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "2"));
            Assert.Equal(StrandedAssetDecision.Regenerate,
                ContractAcceptWorldEffects.DecideStrandedAsset(part, RewindSaveWorld(unowned: false)));
        }

        [Theory]
        // Stock's generators draw a fresh partID; the rebuilt part parameters list the old one.
        [InlineData(true, 4127703291u, 77u, true)]
        [InlineData(true, 4127703291u, 4127703291u, false)]
        [InlineData(true, 4127703291u, 0u, false)]
        // A kerbal-only rescue lists no part, whatever the generator drew.
        [InlineData(false, 4127703291u, 77u, false)]
        public void NeedsPartListRepair_OnlyForAPartRecoveryWhoseIdChanged(
            bool recoveringPart, uint before, uint after, bool expected)
        {
            Assert.Equal(expected,
                ContractAcceptWorldEffects.NeedsPartListRepair(recoveringPart, before, after));
        }

        [Fact]
        public void RebuiltTourism_InTheRewindSaveWorld_ConvertsTheOfferedTourists()
        {
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                "TourismContract", TourismSnapshot("Barfurt Kerman|Stelney Kerman"),
                default(StrandedAssetWorldFacts), name => OfferedTourist(), GameScenes.SPACECENTER);

            Assert.Equal(ContractAcceptRepair.ConvertTourists, plan.Repair);
            Assert.Null(plan.RepairSkipReason);
            Assert.Equal(2, plan.Tourists.Count);
            Assert.All(plan.Tourists, t => Assert.Equal(TouristConversion.Convert, t.Value));
            // A roster write only: any scene.
            Assert.Equal(ContractAcceptRepair.ConvertTourists, ContractAcceptWorldEffects.Plan(
                "TourismContract", TourismSnapshot("Barfurt Kerman"),
                default(StrandedAssetWorldFacts), name => OfferedTourist(), GameScenes.FLIGHT).Repair);
        }

        [Fact]
        public void RebuiltTourism_HeldTouristsAreNeverTouched()
        {
            var facts = new Dictionary<string, TouristRosterFact>
            {
                { "Barfurt Kerman", OfferedTourist() },
                { "Stelney Kerman", new TouristRosterFact { InRoster = true, IsTourist = true } },
                { "Sielan Kerman", new TouristRosterFact { InRoster = true, IsUnowned = true, Reserved = true } },
                { "Jeb Kerman", new TouristRosterFact { InRoster = true } },
            };
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                "TourismContract",
                TourismSnapshot("Barfurt Kerman|Stelney Kerman|Sielan Kerman|Jeb Kerman|Ghost Kerman"),
                default(StrandedAssetWorldFacts),
                name => facts.TryGetValue(name, out var f) ? f : default(TouristRosterFact),
                GameScenes.SPACECENTER);

            Assert.Equal(ContractAcceptRepair.ConvertTourists, plan.Repair);
            Assert.Equal(TouristConversion.Convert, plan.Tourists[0].Value);
            Assert.Equal(TouristConversion.AlreadyTourist, plan.Tourists[1].Value);
            Assert.Equal(TouristConversion.Reserved, plan.Tourists[2].Value);
            Assert.Equal(TouristConversion.NotUnowned, plan.Tourists[3].Value);
            Assert.Equal(TouristConversion.NotInRoster, plan.Tourists[4].Value);
            Assert.Equal("[Sielan Kerman:Reserved, Jeb Kerman:NotUnowned, Ghost Kerman:NotInRoster]",
                plan.RepairSkipReason);

            // Nobody left to convert: no repair, the held ones still named.
            ContractAcceptWorldEffectPlan heldOnly = ContractAcceptWorldEffects.Plan(
                "TourismContract", TourismSnapshot("Sielan Kerman|Stelney Kerman"),
                default(StrandedAssetWorldFacts),
                name => facts[name], GameScenes.SPACECENTER);
            Assert.Equal(ContractAcceptRepair.None, heldOnly.Repair);
            Assert.True(heldOnly.WorldEffectMissing);
            Assert.Equal("[Sielan Kerman:Reserved]", heldOnly.RepairSkipReason);
        }

        [Fact]
        public void RebuiltTourism_TheCommittedTimelineResolvesTheContract_ConvertsNobody()
        {
            // Owner ruling 2026-11-20: the same CommittedResolution guard the rescue side reads.
            var world = new StrandedAssetWorldFacts { CommittedResolution = true };
            foreach (GameScenes scene in new[] { GameScenes.SPACECENTER, GameScenes.FLIGHT })
            {
                ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                    "TourismContract", TourismSnapshot("Barfurt Kerman|Stelney Kerman"),
                    world, name => OfferedTourist(), scene);
                Assert.Equal(ContractAcceptRepair.None, plan.Repair);
                Assert.True(plan.WorldEffectMissing);
                Assert.Equal("missing", plan.Outcome);
                Assert.Equal("CommittedResolution", plan.RepairSkipReason);
            }

            // Nothing to convert: no reason is invented.
            ContractAcceptWorldEffectPlan present = ContractAcceptWorldEffects.Plan(
                "TourismContract", TourismSnapshot("Barfurt Kerman"), world,
                name => new TouristRosterFact { InRoster = true, IsTourist = true }, GameScenes.SPACECENTER);
            Assert.Equal(ContractAcceptRepair.None, present.Repair);
            Assert.Null(present.RepairSkipReason);
        }

        public static IEnumerable<object[]> DrainStepCases()
        {
            // Not a regeneration scene: keep, whatever else holds.
            yield return new object[] { GameScenes.FLIGHT, false, true, (bool?)true, nameof(DeferredRescueDrainStep.KeepNotRegenerationScene) };
            yield return new object[] { GameScenes.EDITOR, true, false, (bool?)null, nameof(DeferredRescueDrainStep.KeepNotRegenerationScene) };
            // A load or a contract patch still running: its own onContractsLoaded drains after it.
            yield return new object[] { GameScenes.SPACECENTER, true, false, (bool?)false, nameof(DeferredRescueDrainStep.KeepLoadOrPatchPending) };
            // No ledger to read yet: keep.
            yield return new object[] { GameScenes.SPACECENTER, false, true, (bool?)null, nameof(DeferredRescueDrainStep.KeepLedgerNotReady) };
            // Stock holds no Active RecoverAsset with the id (completed, failed, removed): drop.
            yield return new object[] { GameScenes.SPACECENTER, false, false, (bool?)true, nameof(DeferredRescueDrainStep.DropContractNotActive) };
            // The ledger no longer holds it accepted by now: the next patch removes it, drop.
            yield return new object[] { GameScenes.TRACKSTATION, false, true, (bool?)false, nameof(DeferredRescueDrainStep.DropNotLedgerActive) };
            yield return new object[] { GameScenes.SPACECENTER, false, true, (bool?)true, nameof(DeferredRescueDrainStep.Evaluate) };
            yield return new object[] { GameScenes.TRACKSTATION, false, true, (bool?)true, nameof(DeferredRescueDrainStep.Evaluate) };
        }

        [Theory]
        [MemberData(nameof(DrainStepCases))]
        public void DecideDeferredRescueDrainStep_FirstMatchWins(
            GameScenes scene, bool loadOrPatchPending, bool activeRescueInStock, bool? ledgerActiveByNow,
            string expected)
        {
            Assert.Equal(expected, ContractAcceptWorldEffects.DecideDeferredRescueDrainStep(
                scene, loadOrPatchPending, activeRescueInStock, ledgerActiveByNow).ToString());
        }

        [Fact]
        public void DeferredRescue_AtTheNextSpaceCenterVisit_RunsTheSameGuardsAsTheRebuild()
        {
            // The drain re-plans from the live contract in the regeneration scene: Regenerate
            // regenerates, and every guard still holds (a vessel that appeared meanwhile, a
            // committed resolution, a reserved kerbal).
            Assert.Equal(ContractAcceptRepair.RegenerateStrandedAsset,
                PlanRescue(RewindSaveWorld(), GameScenes.SPACECENTER).Repair);
            Assert.Equal(ContractAcceptRepair.None,
                PlanRescue(RewindSaveWorld(aboard: true), GameScenes.SPACECENTER).Repair);
            Assert.Equal(ContractAcceptRepair.None,
                PlanRescue(RewindSaveWorld(committedResolution: true), GameScenes.TRACKSTATION).Repair);
            Assert.Equal(ContractAcceptRepair.None,
                PlanRescue(RewindSaveWorld(reserved: true), GameScenes.TRACKSTATION).Repair);
        }

        [Theory]
        [InlineData(false, false, false, false, nameof(TouristConversion.NotInRoster))]
        [InlineData(true, true, false, true, nameof(TouristConversion.AlreadyTourist))]
        [InlineData(true, false, true, true, nameof(TouristConversion.Reserved))]
        [InlineData(true, false, false, false, nameof(TouristConversion.NotUnowned))]
        [InlineData(true, false, true, false, nameof(TouristConversion.Convert))]
        public void DecideTouristConversion_FirstMatchWins(
            bool inRoster, bool isTourist, bool isUnowned, bool reserved, string expected)
        {
            Assert.Equal(expected, ContractAcceptWorldEffects.DecideTouristConversion(new TouristRosterFact
            {
                InRoster = inRoster,
                IsTourist = isTourist,
                IsUnowned = isUnowned,
                Reserved = reserved
            }).ToString());
        }

        [Theory]
        [InlineData("RecoverAsset", nameof(ContractAcceptWorldEffect.StrandedAsset))]
        [InlineData("TourismContract", nameof(ContractAcceptWorldEffect.TouristRoster))]
        [InlineData("OrbitalConstructionContract", nameof(ContractAcceptWorldEffect.PrebuiltShip))]
        [InlineData("RoverConstructionContract", nameof(ContractAcceptWorldEffect.PrebuiltShip))]
        [InlineData("VesselRepairContract", nameof(ContractAcceptWorldEffect.PrebuiltShip))]
        [InlineData("PartTest", nameof(ContractAcceptWorldEffect.ExperimentalPart))]
        // OnAccepted only stamps a launch id into a parameter the snapshot carries.
        [InlineData("BaseContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("SatelliteContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("StationContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("GrandTour", nameof(ContractAcceptWorldEffect.None))]
        // No OnAccepted override.
        [InlineData("ExplorationContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("CollectScience", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("PlantFlag", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("ARMContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("SurveyContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("ISRUContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("CometSampleContract", nameof(ContractAcceptWorldEffect.None))]
        [InlineData("", nameof(ContractAcceptWorldEffect.None))]
        [InlineData(null, nameof(ContractAcceptWorldEffect.None))]
        public void Classify_NamesTheTemplatesWhoseAcceptChangesTheWorld(
            string typeName, string expected)
        {
            Assert.Equal(expected, ContractAcceptWorldEffects.Classify(typeName).ToString());
        }

        [Fact]
        public void ReadRecoverAssetTarget_ReadsTheSnapshotsOwnValues()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.True(t.Readable);
            Assert.True(t.RecoveringKerbal);
            Assert.False(t.RecoveringPart);
            Assert.False(t.Surface);
            Assert.Equal("Rayvan Kerman", t.KerbalName);
            Assert.Equal(4127703291u, t.PartId);

            RecoverAssetTarget compound = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "3", recoveryLocation: "3"));
            Assert.True(compound.RecoveringKerbal);
            Assert.True(compound.RecoveringPart);
            Assert.True(compound.Surface);
        }

        [Fact]
        public void ReadRecoverAssetTarget_UnreadableWithoutTypeLocationOrTarget()
        {
            Assert.False(ContractAcceptWorldEffects.ReadRecoverAssetTarget(null).Readable);
            Assert.False(ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot(recoveryType: null)).Readable);
            Assert.False(ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot(recoveryType: "0")).Readable);
            Assert.False(ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot(recoveryLocation: "0")).Readable);
            // A part recovery with no part id names nothing to look for.
            Assert.False(ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "2", partId: "0")).Readable);

            ContractAcceptWorldEffectPlan plan = PlanRescue(
                RewindSaveWorld(), snapshot: RescueSnapshot(recoveryType: null));
            Assert.Equal("NotReadable", plan.Outcome);
            Assert.False(plan.WorldEffectMissing);
        }

        [Fact]
        public void DecideStrandedAsset_TargetAlreadyInTheWorld_IsPresent_SoARegenerationNeverRunsTwice()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.TargetPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(t, RewindSaveWorld(aboard: true)));
            // Present wins over every hold: nothing is missing.
            Assert.Equal(StrandedAssetDecision.TargetPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(t,
                    RewindSaveWorld(aboard: true, committedResolution: true, reserved: true)));
        }

        [Fact]
        public void DecideStrandedAsset_PartRecovery_ReadsThePartId()
        {
            RecoverAssetTarget part = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "2"));
            Assert.Equal(StrandedAssetDecision.TargetPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(part, RewindSaveWorld(partInWorld: true)));
            // A part recovery needs no kerbal in the roster.
            Assert.Equal(StrandedAssetDecision.Regenerate,
                ContractAcceptWorldEffects.DecideStrandedAsset(part, RewindSaveWorld(inRoster: false)));

            RecoverAssetTarget compound = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "3"));
            Assert.Equal(StrandedAssetDecision.TargetPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound,
                    RewindSaveWorld(aboard: true, partInWorld: true)));
            Assert.Equal(StrandedAssetDecision.Regenerate,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound, RewindSaveWorld()));
        }

        [Fact]
        public void DecideStrandedAsset_CompoundRescueHalfInTheWorld_IsNeverRegenerated()
        {
            // Stock's generators make the kerbal and the part together: a regeneration with the
            // kerbal already aboard a vessel would seat him in a second one (GenerateKerbalInPart
            // -> SeatKerbal), and one with the part present would add a second copy of it. The
            // snapshot's partID can also be stale after a regeneration re-drew it.
            RecoverAssetTarget compound = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "3"));
            Assert.Equal(StrandedAssetDecision.PartiallyPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound, RewindSaveWorld(aboard: true)));
            Assert.Equal(StrandedAssetDecision.PartiallyPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound, RewindSaveWorld(partInWorld: true)));
            RecoverAssetTarget surfaceCompound = ContractAcceptWorldEffects.ReadRecoverAssetTarget(
                RescueSnapshot(recoveryType: "3", recoveryLocation: "3"));
            Assert.Equal(StrandedAssetDecision.PartiallyPresent,
                ContractAcceptWorldEffects.DecideStrandedAsset(surfaceCompound, RewindSaveWorld(aboard: true)));

            foreach (GameScenes scene in new[] { GameScenes.SPACECENTER, GameScenes.FLIGHT })
            {
                ContractAcceptWorldEffectPlan plan = PlanRescue(RewindSaveWorld(aboard: true), scene,
                    RescueSnapshot(recoveryType: "3"));
                Assert.Equal(ContractAcceptRepair.None, plan.Repair);
                Assert.True(plan.WorldEffectMissing);
                Assert.Equal("PartiallyPresent", plan.Outcome);
                Assert.Equal("PartiallyPresent", plan.RepairSkipReason);
            }
        }

        [Fact]
        public void DecideStrandedAsset_TheCommittedTimelineOwnsTheOutcome_NoSecondCopy()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.CommittedResolution,
                ContractAcceptWorldEffects.DecideStrandedAsset(t,
                    RewindSaveWorld(committedResolution: true, reserved: true, inRoster: false)));
            Assert.Equal(StrandedAssetDecision.KerbalReserved,
                ContractAcceptWorldEffects.DecideStrandedAsset(t,
                    RewindSaveWorld(reserved: true, inRoster: false)));
        }

        [Fact]
        public void DecideStrandedAsset_KerbalNotInRoster_WhenTheOfferPostdatesTheRewindSave()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.KerbalNotInRoster,
                ContractAcceptWorldEffects.DecideStrandedAsset(t, RewindSaveWorld(inRoster: false)));
            // Still a missing world effect: the contract names a kerbal no world holds.
            Assert.True(PlanRescue(RewindSaveWorld(inRoster: false)).WorldEffectMissing);
        }

        [Fact]
        public void Tourism_TouristsNotConvertedByTheRebuild_AreMissing()
        {
            ConfigNode n = TourismSnapshot("Barfurt Kerman|Stelney Kerman| Sielan Kerman|Barfurt Kerman");
            Assert.Equal(new List<string> { "Barfurt Kerman", "Stelney Kerman", "Sielan Kerman" },
                ContractAcceptWorldEffects.ReadTourists(n));

            // The rewind save holds them as the offer made them: Unowned, not Tourist.
            ContractAcceptWorldEffectPlan missing = ContractAcceptWorldEffects.Plan(
                "TourismContract", n, default(StrandedAssetWorldFacts),
                name => name == "Stelney Kerman"
                    ? new TouristRosterFact { InRoster = true, IsTourist = true }
                    : OfferedTourist(),
                GameScenes.SPACECENTER);
            Assert.Equal(ContractAcceptWorldEffect.TouristRoster, missing.Effect);
            Assert.True(missing.WorldEffectMissing);
            Assert.Equal("missing", missing.Outcome);
            Assert.Contains("Barfurt Kerman, Sielan Kerman", missing.Detail);

            ContractAcceptWorldEffectPlan present = ContractAcceptWorldEffects.Plan(
                "TourismContract", n, default(StrandedAssetWorldFacts),
                name => new TouristRosterFact { InRoster = true, IsTourist = true }, GameScenes.SPACECENTER);
            Assert.False(present.WorldEffectMissing);
            Assert.Equal("present", present.Outcome);
            Assert.Equal(ContractAcceptRepair.None, present.Repair);

            Assert.Equal("unchecked", ContractAcceptWorldEffects.Plan(
                "TourismContract", TourismSnapshot(null), default(StrandedAssetWorldFacts), null,
                GameScenes.SPACECENTER).Outcome);
        }

        [Fact]
        public void UnreadableConstructionAndPartTest_AreNotReadable_AndNoneIsNone()
        {
            var n = new ConfigNode("CONTRACT");
            ContractAcceptWorldEffectPlan repair = ContractAcceptWorldEffects.Plan(
                "VesselRepairContract", n, default(StrandedAssetWorldFacts), null, GameScenes.SPACECENTER);
            Assert.Equal(nameof(PrebuiltShipDecision.NotReadable), repair.Outcome);
            Assert.False(repair.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, repair.Repair);
            ContractAcceptWorldEffectPlan test = ContractAcceptWorldEffects.Plan(
                "PartTest", n, default(StrandedAssetWorldFacts), null, GameScenes.SPACECENTER);
            Assert.Equal(nameof(ExperimentalPartDecision.NotReadable), test.Outcome);
            Assert.False(test.WorldEffectMissing);
            ContractAcceptWorldEffectPlan none = ContractAcceptWorldEffects.Plan(
                "ExplorationContract", n, default(StrandedAssetWorldFacts), null, GameScenes.SPACECENTER);
            Assert.Equal(ContractAcceptWorldEffect.None, none.Effect);
            Assert.Equal("none", none.Outcome);
            Assert.False(none.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, none.Repair);
        }

        // --- Prebuilt ship (OrbitalConstruction / RoverConstruction / VesselRepair) ---------

        private const uint FirstTimelinePid = 2817366511u;
        private const string RepairCraft =
            "GameData/Squad/Contracts/PreBuiltCraft/RepairContract/Repair Contract Sat 9.craft";
        private const string RepairVesselName = "Damaged Kerbin AS-P Satellite NV5SQ";

        // A VesselRepairContract as the rebuild's Contract.Load leaves it (c1's offer shape, plus
        // the repairVslId the accept's own spawn handler stored before onAccepted fired).
        private static ConfigNode RepairContractLive(uint vesselPid = FirstTimelinePid, string craft = RepairCraft)
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("guid", "675054ef-61bb-4d33-b0d6-df5d73b0e9fa");
            n.AddValue("type", "VesselRepairContract");
            n.AddValue("state", "Active");
            n.AddValue("bodyName", "Kerbin");
            if (craft != null) n.AddValue("roverCraftDef", craft);
            n.AddValue("vesselName", RepairVesselName);
            if (vesselPid != 0) n.AddValue("repairVslId", vesselPid.ToString());
            n.AddValue("repairPartName", "largeSolarPanel");
            return n;
        }

        // The rewind save's world: the accept that built the satellite lies after the save.
        private static PrebuiltShipWorldFacts EmptyPrebuiltWorld(
            bool pidPresent = false, bool namePresent = false, bool recordedByCommittedFlight = false)
        {
            return new PrebuiltShipWorldFacts
            {
                VesselPidInWorld = pid => pidPresent && pid == FirstTimelinePid,
                VesselNamedInWorld = name => namePresent && name == RepairVesselName,
                VesselRecordedByCommittedFlight = pid => recordedByCommittedFlight && pid == FirstTimelinePid
            };
        }

        private static ContractAcceptWorldEffectPlan PlanRepair(
            PrebuiltShipWorldFacts world, GameScenes scene = GameScenes.SPACECENTER,
            bool committedResolution = false, ConfigNode live = null)
        {
            return ContractAcceptWorldEffects.Plan(
                "VesselRepairContract", live ?? RepairContractLive(),
                new StrandedAssetWorldFacts { CommittedResolution = committedResolution }, null, scene,
                world, default(ExperimentalPartFacts));
        }

        [Fact]
        public void RebuiltRepairContract_InTheRewindSaveWorld_AtTheSpaceCenter_SpawnsItsCraft()
        {
            ContractAcceptWorldEffectPlan plan = PlanRepair(EmptyPrebuiltWorld());
            Assert.Equal(ContractAcceptWorldEffect.PrebuiltShip, plan.Effect);
            Assert.Equal(nameof(PrebuiltShipDecision.Regenerate), plan.Outcome);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.SpawnPrebuiltShip, plan.Repair);
            Assert.Null(plan.RepairSkipReason);
            Assert.Equal(FirstTimelinePid, plan.Prebuilt.VesselPid);
            Assert.Contains("vesselPid=2817366511", plan.Detail);
            Assert.Contains("Repair Contract Sat 9.craft", plan.Detail);
        }

        [Theory]
        [InlineData(GameScenes.FLIGHT)]
        [InlineData(GameScenes.EDITOR)]
        [InlineData(GameScenes.TRACKSTATION)]
        public void RebuiltRepairContract_OutsideTheSpaceCenter_IsDeferredToTheNextVisit(GameScenes scene)
        {
            ContractAcceptWorldEffectPlan plan = PlanRepair(EmptyPrebuiltWorld(), scene);
            Assert.Equal(nameof(PrebuiltShipDecision.Regenerate), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.DeferPrebuiltShip, plan.Repair);
            Assert.Equal("scene=" + scene, plan.RepairSkipReason);
        }

        [Fact]
        public void RebuiltRepairContract_ItsCraftInTheWorld_ByIdOrByName_IsLeftAlone()
        {
            ContractAcceptWorldEffectPlan byId = PlanRepair(EmptyPrebuiltWorld(pidPresent: true));
            Assert.Equal(nameof(PrebuiltShipDecision.TargetPresent), byId.Outcome);
            Assert.False(byId.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, byId.Repair);
            Assert.Null(byId.RepairSkipReason);

            ContractAcceptWorldEffectPlan byName = PlanRepair(EmptyPrebuiltWorld(namePresent: true));
            Assert.Equal(nameof(PrebuiltShipDecision.TargetPresent), byName.Outcome);
            Assert.Equal(ContractAcceptRepair.None, byName.Repair);
        }

        [Fact]
        public void RebuiltRepairContract_PresentBeatsEveryOtherGuard()
        {
            ContractAcceptWorldEffectPlan plan = PlanRepair(
                EmptyPrebuiltWorld(pidPresent: true, recordedByCommittedFlight: true),
                GameScenes.FLIGHT, committedResolution: true);
            Assert.Equal(nameof(PrebuiltShipDecision.TargetPresent), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
        }

        [Fact]
        public void RebuiltRepairContract_TheCommittedTimelineResolvesIt_SpawnsNothing()
        {
            ContractAcceptWorldEffectPlan plan = PlanRepair(EmptyPrebuiltWorld(), committedResolution: true);
            Assert.Equal(nameof(PrebuiltShipDecision.CommittedResolution), plan.Outcome);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
            Assert.Equal(nameof(PrebuiltShipDecision.CommittedResolution), plan.RepairSkipReason);
        }

        [Fact]
        public void RebuiltRepairContract_ACommittedFlightRecordsItsCraft_SpawnsNothing()
        {
            ContractAcceptWorldEffectPlan plan = PlanRepair(EmptyPrebuiltWorld(recordedByCommittedFlight: true));
            Assert.Equal(nameof(PrebuiltShipDecision.VesselRecordedByCommittedFlight), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
            Assert.Equal(nameof(PrebuiltShipDecision.VesselRecordedByCommittedFlight), plan.RepairSkipReason);
        }

        [Fact]
        public void RebuiltRepairContract_TheFirstAcceptBuiltNothing_HasNothingToRestore()
        {
            ContractAcceptWorldEffectPlan noId = PlanRepair(EmptyPrebuiltWorld(), live: RepairContractLive(vesselPid: 0));
            Assert.Equal(nameof(PrebuiltShipDecision.NoSpawnRecorded), noId.Outcome);
            Assert.False(noId.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, noId.Repair);

            ContractAcceptWorldEffectPlan noCraft = PlanRepair(EmptyPrebuiltWorld(), live: RepairContractLive(craft: null));
            Assert.Equal(nameof(PrebuiltShipDecision.NotReadable), noCraft.Outcome);
            Assert.Equal(ContractAcceptRepair.None, noCraft.Repair);
        }

        [Theory]
        [InlineData("OrbitalConstructionContract", "constructionCraftDef", "constructionVslId", true)]
        [InlineData("RoverConstructionContract", "roverCraftDef", "roverVslId", false)]
        [InlineData("VesselRepairContract", "roverCraftDef", "repairVslId", true)]
        public void ReadPrebuiltShipTarget_ReadsEachTemplatesOwnKeys(
            string type, string craftKey, string idKey, bool savesName)
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue(craftKey, "craft.craft");
            n.AddValue(idKey, "77");
            if (savesName) n.AddValue("vesselName", "Station X");
            PrebuiltShipTarget t = ContractAcceptWorldEffects.ReadPrebuiltShipTarget(type, n);
            Assert.True(t.Readable);
            Assert.Equal("craft.craft", t.CraftUrl);
            Assert.Equal(77u, t.VesselPid);
            Assert.Equal(savesName ? "Station X" : null, t.VesselName);

            // Another template's id key is not read.
            var other = new ConfigNode("CONTRACT");
            other.AddValue(craftKey, "craft.craft");
            other.AddValue(idKey == "repairVslId" ? "roverVslId" : "repairVslId", "77");
            Assert.Equal(0u, ContractAcceptWorldEffects.ReadPrebuiltShipTarget(type, other).VesselPid);
        }

        [Theory]
        [InlineData("PrebuiltShip", GameScenes.SPACECENTER, "Evaluate")]
        [InlineData("PrebuiltShip", GameScenes.TRACKSTATION, "KeepNotRegenerationScene")]
        [InlineData("PrebuiltShip", GameScenes.FLIGHT, "KeepNotRegenerationScene")]
        [InlineData("StrandedAsset", GameScenes.TRACKSTATION, "Evaluate")]
        public void DecideDeferredDrainStep_APrebuiltShipWaitsForTheSpaceCenter(
            string effect, GameScenes scene, string expected)
        {
            var e = (ContractAcceptWorldEffect)System.Enum.Parse(typeof(ContractAcceptWorldEffect), effect);
            Assert.Equal(expected, ContractAcceptWorldEffects.DecideDeferredRescueDrainStep(
                e, scene, false, true, true).ToString());
        }

        // --- Experimental part (PartTest) ---------------------------------------------------

        private static ConfigNode PartTestSnapshot(string exp = "True", string part = "liquidEngine2.v2")
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("guid", "7a726c83-8544-4b20-8818-73762962a34b");
            n.AddValue("type", "PartTest");
            n.AddValue("state", "Active");
            if (part != null) n.AddValue("part", part);
            n.AddValue("haul", "False");
            n.AddValue("dest", "1");
            n.AddValue("sit", "PRELAUNCH");
            if (exp != null) n.AddValue("exp", exp);
            return n;
        }

        // The rewind save's R&D: the accept that made the part experimental lies after the save.
        private static ExperimentalPartFacts RewindSaveRnD(
            int count = 0, int others = 0, bool purchased = false, bool known = true)
        {
            return new ExperimentalPartFacts
            {
                PartKnown = name => known,
                PartPurchased = name => purchased,
                ExperimentalCount = name => count,
                OtherExperimentalTests = name => others
            };
        }

        private static ContractAcceptWorldEffectPlan PlanPartTest(
            ExperimentalPartFacts rnd, bool committedResolution = false, ConfigNode snapshot = null,
            GameScenes scene = GameScenes.FLIGHT)
        {
            return ContractAcceptWorldEffects.Plan(
                "PartTest", snapshot ?? PartTestSnapshot(),
                new StrandedAssetWorldFacts { CommittedResolution = committedResolution }, null, scene,
                default(PrebuiltShipWorldFacts), rnd);
        }

        [Theory]
        [InlineData(GameScenes.SPACECENTER)]
        [InlineData(GameScenes.FLIGHT)]
        [InlineData(GameScenes.EDITOR)]
        public void RebuiltPartTest_InTheRewindSaveRnD_AddsItsExperimentalPart_InAnyScene(GameScenes scene)
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(), scene: scene);
            Assert.Equal(ContractAcceptWorldEffect.ExperimentalPart, plan.Effect);
            Assert.Equal(nameof(ExperimentalPartDecision.Add), plan.Outcome);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.AddExperimentalPart, plan.Repair);
            Assert.Equal("liquidEngine2.v2", plan.Experimental.PartName);
            Assert.Contains("rndCount=0 otherTests=0", plan.Detail);
        }

        [Theory]
        [InlineData(1, 0, "Present")]
        [InlineData(2, 1, "Present")]
        [InlineData(1, 1, "Add")]
        [InlineData(0, 2, "Add")]
        public void RebuiltPartTest_CountsOneShareForItselfAndEachOtherActiveTest(int count, int others, string outcome)
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(count, others));
            Assert.Equal(outcome, plan.Outcome);
            Assert.Equal(outcome == "Add" ? ContractAcceptRepair.AddExperimentalPart : ContractAcceptRepair.None,
                plan.Repair);
        }

        [Fact]
        public void RebuiltPartTest_ThatAddedNothingAtAccept_NeedsNothing()
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(), snapshot: PartTestSnapshot(exp: "False"));
            Assert.Equal(nameof(ExperimentalPartDecision.NotExperimentalAtAccept), plan.Outcome);
            Assert.False(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
        }

        [Fact]
        public void RebuiltPartTest_ThePartIsPurchasedNow_NeedsNothing()
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(purchased: true));
            Assert.Equal(nameof(ExperimentalPartDecision.PartPurchased), plan.Outcome);
            Assert.False(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
        }

        [Fact]
        public void RebuiltPartTest_TheCommittedTimelineResolvesIt_AddsNothing()
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(), committedResolution: true);
            Assert.Equal(nameof(ExperimentalPartDecision.CommittedResolution), plan.Outcome);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
            Assert.Equal(nameof(ExperimentalPartDecision.CommittedResolution), plan.RepairSkipReason);
        }

        [Fact]
        public void RebuiltPartTest_PresentBeatsPurchasedAndCommittedResolution()
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(count: 1, purchased: true),
                committedResolution: true);
            Assert.Equal(nameof(ExperimentalPartDecision.Present), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
        }

        [Fact]
        public void RebuiltPartTest_RnDCountUnreadable_IsNamedAndLeftAlone()
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(count: -1));
            Assert.Equal(nameof(ExperimentalPartDecision.RnDNotReadable), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
            Assert.Equal(nameof(ExperimentalPartDecision.RnDNotReadable), plan.RepairSkipReason);
        }

        [Theory]
        [InlineData("Part", "True", true)]
        [InlineData(null, "True", true)]
        [InlineData("liquidEngine2.v2", null, true)]
        [InlineData("liquidEngine2.v2", "maybe", true)]
        [InlineData("liquidEngine2.v2", "True", false)]
        public void RebuiltPartTest_UnreadableOrUnloadedPart_IsNotReadable(string part, string exp, bool known)
        {
            ContractAcceptWorldEffectPlan plan = PlanPartTest(RewindSaveRnD(known: known),
                snapshot: PartTestSnapshot(exp: exp, part: part));
            Assert.Equal(nameof(ExperimentalPartDecision.NotReadable), plan.Outcome);
            Assert.Equal(ContractAcceptRepair.None, plan.Repair);
        }
        // A stand-in for the stock templates, which declare the flag protected on the
        // concrete type; the helper walks the hierarchy, so a subclass reaches it too.
        private class FakePrebuiltTemplate
        {
            protected bool waitingForVslSpawn;
            public FakePrebuiltTemplate(bool waiting) { waitingForVslSpawn = waiting; }
            public bool Waiting => waitingForVslSpawn;
        }

        private class FakePrebuiltSubclass : FakePrebuiltTemplate
        {
            public FakePrebuiltSubclass() : base(true) { }
        }

        [Fact]
        public void ClearPrebuiltSpawnWait_ClearsASetFlag_SoTheContractAdoptsNoLaterCraft()
        {
            var c = new FakePrebuiltTemplate(true);
            Assert.Equal("cleared", ContractAcceptWorldEffects.ClearPrebuiltSpawnWait(c));
            Assert.False(c.Waiting);
        }

        [Fact]
        public void ClearPrebuiltSpawnWait_ReachesTheFlagDeclaredOnABaseType()
        {
            var c = new FakePrebuiltSubclass();
            Assert.Equal("cleared", ContractAcceptWorldEffects.ClearPrebuiltSpawnWait(c));
            Assert.False(c.Waiting);
        }

        [Fact]
        public void ClearPrebuiltSpawnWait_ReportsClearNoFieldAndNoContract()
        {
            Assert.Equal("already-clear",
                ContractAcceptWorldEffects.ClearPrebuiltSpawnWait(new FakePrebuiltTemplate(false)));
            Assert.Equal("no-field", ContractAcceptWorldEffects.ClearPrebuiltSpawnWait(new object()));
            Assert.Equal("no-contract", ContractAcceptWorldEffects.ClearPrebuiltSpawnWait(null));
        }
    }
}
