using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS. The contract patch rebuilds a committed
    /// accept from its snapshot with <c>Contract.Load</c>, which never runs the template's
    /// <c>OnAccepted</c> (decompiled KSP 1.12.5): a RecoverAsset's stranded vessel and a
    /// TourismContract's tourist conversion are not rebuilt. These pin the pure classification
    /// and the guard a regeneration must pass.
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
            bool committedResolution = false, bool namedByCommittedFlight = false,
            bool inRoster = true, bool aboard = false, bool partInWorld = false)
        {
            return new StrandedAssetWorldFacts
            {
                PartInAnyVessel = id => partInWorld,
                KerbalAboardAnyVessel = name => aboard && name == "Rayvan Kerman",
                KerbalInRoster = name => inRoster && name == "Rayvan Kerman",
                KerbalNamedByCommittedFlight = name => namedByCommittedFlight,
                CommittedResolution = committedResolution
            };
        }

        [Fact]
        public void RebuiltRescue_InTheRewindSaveWorld_IsMissingItsStrandedVessel()
        {
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                "RecoverAsset", RescueSnapshot(), RewindSaveWorld(), name => false);

            Assert.Equal(ContractAcceptWorldEffect.StrandedAsset, plan.Effect);
            Assert.True(plan.WorldEffectMissing);
            Assert.Equal("Regenerate", plan.Outcome);
            Assert.Contains("kerbal='Rayvan Kerman'", plan.Detail);
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

            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                "RecoverAsset", RescueSnapshot(recoveryType: null), RewindSaveWorld(), null);
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
                    RewindSaveWorld(aboard: true, committedResolution: true, namedByCommittedFlight: true)));
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
            Assert.Equal(StrandedAssetDecision.Regenerate,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound, RewindSaveWorld(aboard: true)));
            Assert.Equal(StrandedAssetDecision.Regenerate,
                ContractAcceptWorldEffects.DecideStrandedAsset(compound, RewindSaveWorld(partInWorld: true)));
        }

        [Fact]
        public void DecideStrandedAsset_TheCommittedTimelineOwnsTheOutcome_NoSecondCopy()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.CommittedResolution,
                ContractAcceptWorldEffects.DecideStrandedAsset(t,
                    RewindSaveWorld(committedResolution: true, namedByCommittedFlight: true, inRoster: false)));
            Assert.Equal(StrandedAssetDecision.KerbalHeldByCommittedFlight,
                ContractAcceptWorldEffects.DecideStrandedAsset(t,
                    RewindSaveWorld(namedByCommittedFlight: true, inRoster: false)));
        }

        [Fact]
        public void DecideStrandedAsset_KerbalNotInRoster_WhenTheOfferPostdatesTheRewindSave()
        {
            RecoverAssetTarget t = ContractAcceptWorldEffects.ReadRecoverAssetTarget(RescueSnapshot());
            Assert.Equal(StrandedAssetDecision.KerbalNotInRoster,
                ContractAcceptWorldEffects.DecideStrandedAsset(t, RewindSaveWorld(inRoster: false)));
            // Still a missing world effect: the contract names a kerbal no world holds.
            Assert.True(ContractAcceptWorldEffects.Plan(
                "RecoverAsset", RescueSnapshot(), RewindSaveWorld(inRoster: false), null).WorldEffectMissing);
        }

        [Fact]
        public void Tourism_TouristsNotConvertedByTheRebuild_AreMissing()
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("type", "TourismContract");
            n.AddValue("tourists", "Barfurt Kerman|Stelney Kerman| Sielan Kerman|Barfurt Kerman");
            Assert.Equal(new List<string> { "Barfurt Kerman", "Stelney Kerman", "Sielan Kerman" },
                ContractAcceptWorldEffects.ReadTourists(n));

            // The rewind save holds them as the offer made them: Unowned, not Tourist.
            ContractAcceptWorldEffectPlan missing = ContractAcceptWorldEffects.Plan(
                "TourismContract", n, default(StrandedAssetWorldFacts), name => name == "Stelney Kerman");
            Assert.Equal(ContractAcceptWorldEffect.TouristRoster, missing.Effect);
            Assert.True(missing.WorldEffectMissing);
            Assert.Equal("missing", missing.Outcome);
            Assert.Contains("Barfurt Kerman, Sielan Kerman", missing.Detail);

            ContractAcceptWorldEffectPlan present = ContractAcceptWorldEffects.Plan(
                "TourismContract", n, default(StrandedAssetWorldFacts), name => true);
            Assert.False(present.WorldEffectMissing);
            Assert.Equal("present", present.Outcome);

            var none = new ConfigNode("CONTRACT");
            Assert.Equal("unchecked", ContractAcceptWorldEffects.Plan(
                "TourismContract", none, default(StrandedAssetWorldFacts), null).Outcome);
        }

        [Fact]
        public void PrebuiltShipAndExperimentalPart_AreUnchecked_AndNoneIsNone()
        {
            var n = new ConfigNode("CONTRACT");
            Assert.Equal("unchecked", ContractAcceptWorldEffects.Plan(
                "VesselRepairContract", n, default(StrandedAssetWorldFacts), null).Outcome);
            Assert.Equal("unchecked", ContractAcceptWorldEffects.Plan(
                "PartTest", n, default(StrandedAssetWorldFacts), null).Outcome);
            ContractAcceptWorldEffectPlan none = ContractAcceptWorldEffects.Plan(
                "ExplorationContract", n, default(StrandedAssetWorldFacts), null);
            Assert.Equal(ContractAcceptWorldEffect.None, none.Effect);
            Assert.Equal("none", none.Outcome);
            Assert.False(none.WorldEffectMissing);
        }
    }
}
