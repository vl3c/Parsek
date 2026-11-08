using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless guards for wheel damage recording and ghost playback. Stock ModuleWheelDamage keeps
    /// a persistent <c>isDamaged</c> flag; on a break it hides the intact wheel transform
    /// (<c>undamagedTransformName</c>) and shows the busted one (<c>damagedTransformName</c>), and an
    /// EVA engineer's repair swaps them back. Before these events existed a ghost always drove on an
    /// intact wheel. The Unity half (the cloned transforms actually swapping) is the in-game
    /// <c>PartEventFidelity</c> cell; everything decidable without Unity is pinned here.
    /// </summary>
    [Collection("Sequential")]
    public class WheelDamageRecordingTests : IDisposable
    {
        private const uint WheelPid = 100000u;
        private const uint OtherPid = 101111u;
        private const string WheelName = "roverWheel1";

        private readonly List<string> logLines = new List<string>();

        public WheelDamageRecordingTests()
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

        private static Dictionary<uint, string> Names()
            => new Dictionary<uint, string> { { WheelPid, WheelName } };

        private static PartEvent Evt(double ut, PartEventType type, uint pid = WheelPid)
            => new PartEvent { ut = ut, partPersistentId = pid, eventType = type, partName = WheelName };

        #region Enum stability

        [Fact]
        public void WheelMembers_AreExplicitlyNumbered45And46()
        {
            // Raw ints in .prec sidecars: a renumber is silent cross-version corruption.
            Assert.Equal(45, (int)PartEventType.WheelBroken);
            Assert.Equal(46, (int)PartEventType.WheelRepaired);
            // The legacy text codec admits a type through Enum.IsDefined.
            Assert.True(Enum.IsDefined(typeof(PartEventType), 45));
            Assert.True(Enum.IsDefined(typeof(PartEventType), 46));
            // The P8 tail the append must not have moved.
            Assert.Equal(44, (int)PartEventType.EvaRagdollEnded);
        }

        #endregion

        #region Recorder truth table

        [Theory]
        // isDamaged, wasTracked, expected event (null = none), tracked after
        [InlineData(true, false, PartEventType.WheelBroken, true)]
        [InlineData(false, true, PartEventType.WheelRepaired, false)]
        [InlineData(true, true, null, true)]
        [InlineData(false, false, null, false)]
        public void CheckWheelDamageTransition_TruthTable(
            bool isDamaged, bool wasTracked, PartEventType? expected, bool trackedAfter)
        {
            var set = new HashSet<uint>();
            if (wasTracked) set.Add(WheelPid);

            PartEvent? evt = FlightRecorder.CheckWheelDamageTransition(
                WheelPid, WheelName, isDamaged, set, 123.0);

            if (expected.HasValue)
            {
                Assert.True(evt.HasValue);
                Assert.Equal(expected.Value, evt.Value.eventType);
                Assert.Equal(WheelPid, evt.Value.partPersistentId);
                Assert.Equal(WheelName, evt.Value.partName);
                Assert.Equal(123.0, evt.Value.ut);
            }
            else
            {
                Assert.False(evt.HasValue);
            }
            Assert.Equal(trackedAfter, set.Contains(WheelPid));
        }

        [Fact]
        public void CheckWheelDamageTransition_NullSet_EmitsNothing()
        {
            Assert.False(FlightRecorder.CheckWheelDamageTransition(
                WheelPid, WheelName, true, null, 1.0).HasValue);
        }

        [Fact]
        public void ABreakThenEvaRepair_ProducesExactlyTwoEvents_TheStatedBudget()
        {
            var set = new HashSet<uint>();
            var events = new List<PartEvent>();
            bool[] polled = { false, false, true, true, true, false, false };
            for (int i = 0; i < polled.Length; i++)
            {
                PartEvent? evt = FlightRecorder.CheckWheelDamageTransition(
                    WheelPid, WheelName, polled[i], set, 100.0 + i);
                if (evt.HasValue) events.Add(evt.Value);
            }

            Assert.Equal(2, events.Count);
            Assert.Equal(PartEventType.WheelBroken, events[0].eventType);
            Assert.Equal(102.0, events[0].ut);
            Assert.Equal(PartEventType.WheelRepaired, events[1].eventType);
            Assert.Equal(105.0, events[1].ut);
        }

        #endregion

        #region Start-of-recording seeding

        [Fact]
        public void ARecordingThatStartsWithABrokenWheel_SeedsWheelBrokenAtStart()
        {
            var sets = new PartTrackingSets();
            sets.damagedWheelParts.Add(WheelPid);

            var events = PartStateSeeder.EmitSeedEvents(sets, Names(), startUT: 1000.0, logTag: "Recorder");

            PartEvent seed = Assert.Single(events.Where(e => e.eventType == PartEventType.WheelBroken));
            Assert.Equal(WheelPid, seed.partPersistentId);
            Assert.Equal(1000.0, seed.ut);
            Assert.Equal(WheelName, seed.partName);
            Assert.Empty(events.Where(e => e.eventType == PartEventType.WheelRepaired));
        }

        [Fact]
        public void ARecordingThatStartsIntact_SeedsNoWheelEvent()
        {
            var events = PartStateSeeder.EmitSeedEvents(
                new PartTrackingSets(), Names(), startUT: 1000.0, logTag: "Recorder");

            Assert.Empty(events.Where(e => e.eventType == PartEventType.WheelBroken
                || e.eventType == PartEventType.WheelRepaired));
        }

        #endregion

        #region Rails-span reconcile

        [Fact]
        public void AWheelBrokenOrRepairedAcrossARailsSpan_ReconcilesInBothDirections()
        {
            var broken = new PartTrackingSets();
            broken.damagedWheelParts.Add(WheelPid);
            var intact = new PartTrackingSets();

            var broke = PartStateSeeder.EmitDiffEvents(intact, broken, Names(), 2000.0, "BgRecorder");
            PartEvent b = Assert.Single(broke);
            Assert.Equal(PartEventType.WheelBroken, b.eventType);
            Assert.Equal(2000.0, b.ut);

            var repaired = PartStateSeeder.EmitDiffEvents(broken, intact, Names(), 2000.0, "BgRecorder");
            Assert.Equal(PartEventType.WheelRepaired, Assert.Single(repaired).eventType);

            // Broken on both sides of the span is not a change.
            Assert.Empty(PartStateSeeder.EmitDiffEvents(
                broken, PartStateSeeder.ClonePartTrackingSets(broken), Names(), 2000.0, "BgRecorder"));
        }

        [Fact]
        public void CloningTrackingSets_CarriesTheDamagedWheelSetByValue()
        {
            var source = new PartTrackingSets();
            source.damagedWheelParts.Add(WheelPid);

            PartTrackingSets clone = PartStateSeeder.ClonePartTrackingSets(source);
            source.damagedWheelParts.Clear();

            Assert.Contains(WheelPid, clone.damagedWheelParts);
            Assert.NotSame(source.damagedWheelParts, clone.damagedWheelParts);
        }

        #endregion

        #region Split seeds

        [Fact]
        public void ATailAfterABreak_SeedsWheelBroken()
        {
            var seeds = RecordingOptimizer.BuildTransientStateSeeds(
                new List<PartEvent> { Evt(200, PartEventType.WheelBroken) }, splitUT: 300.0);

            PartEvent seed = Assert.Single(seeds.Where(s => s.partPersistentId == WheelPid));
            Assert.Equal(PartEventType.WheelBroken, seed.eventType);
            Assert.Equal(300.0, seed.ut);
        }

        [Fact]
        public void ATailAfterABreakThenRepair_SeedsWheelRepaired()
        {
            // The repaired direction is not redundant: a split TIP's snapshot is a copy of the
            // launch-time one, so a rover that launched broken and was repaired in the head span
            // would otherwise spawn its tail ghost broken.
            var seeds = RecordingOptimizer.BuildTransientStateSeeds(
                new List<PartEvent>
                {
                    Evt(100, PartEventType.WheelBroken),
                    Evt(200, PartEventType.WheelRepaired),
                }, splitUT: 300.0);

            PartEvent seed = Assert.Single(seeds.Where(s => s.partPersistentId == WheelPid));
            Assert.Equal(PartEventType.WheelRepaired, seed.eventType);
        }

        [Fact]
        public void WheelDamageIsItsOwnSeedFamily_SoALandingGearKeepsBothItsPoseAndItsDamage()
        {
            // Landing gear carries both ModuleWheelDeployment and ModuleWheelDamage on one part:
            // the two opinions must not overwrite each other in the pid-keyed reducer.
            var seeds = RecordingOptimizer.BuildTransientStateSeeds(
                new List<PartEvent>
                {
                    Evt(100, PartEventType.GearDeployed),
                    Evt(150, PartEventType.WheelBroken),
                }, splitUT: 300.0);

            var forWheel = seeds.Where(s => s.partPersistentId == WheelPid).ToList();
            Assert.Equal(2, forWheel.Count);
            Assert.Contains(forWheel, s => s.eventType == PartEventType.GearDeployed);
            Assert.Contains(forWheel, s => s.eventType == PartEventType.WheelBroken);
        }

        #endregion

        #region Classifiers

        [Fact]
        public void WheelEvents_AreGhostingTriggers_AndPrewarmAHiddenGhost()
        {
            foreach (var type in new[] { PartEventType.WheelBroken, PartEventType.WheelRepaired })
            {
                Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(type), type + " swaps a mesh");
                Assert.True(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(type),
                    type + " must prewarm a hidden ghost, or it shows the wrong wheel when it appears");
            }
            // An explicit arm, not the unknown-type default (which logs "unknown PartEventType").
            Assert.DoesNotContain(logLines, l => l.Contains("unknown PartEventType"));
        }

        [Fact]
        public void WheelEvents_AreReversible_NotPermanent_AndNotInertForTailTrim()
        {
            foreach (var type in new[] { PartEventType.WheelBroken, PartEventType.WheelRepaired })
            {
                Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(type));
                Assert.False(RecordingOptimizer.IsInertPartEventForTailTrim(Evt(1, type)));
                // A break or an EVA repair is a real change: a resumed segment holding one is kept.
                Assert.True(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(Evt(1, type)));
            }
        }

        [Fact]
        public void TheApplyLogHasAWheelDamageSurfaceToken()
        {
            Assert.Equal("wheel-damage",
                GhostPartEventApplyLog.SurfaceToken(GhostPartEventSurface.WheelDamage));
        }

        #endregion

        #region Snapshot baseline

        private static ConfigNode WheelPartNode(string isDamaged, bool withGear = false)
        {
            var partNode = new ConfigNode("PART");
            if (withGear)
            {
                ConfigNode gear = partNode.AddNode("MODULE");
                gear.AddValue("name", "ModuleWheelDeployment");
                gear.AddValue("stateString", "Deployed");
            }
            ConfigNode module = partNode.AddNode("MODULE");
            module.AddValue("name", "ModuleWheelDamage");
            if (isDamaged != null)
                module.AddValue("isDamaged", isDamaged);
            return partNode;
        }

        [Fact]
        public void ASnapshotWithABrokenWheel_FillsTheWheelBaseline()
        {
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(WheelPartNode("True"));

            Assert.NotNull(baseline);
            Assert.True(baseline.wheelDamaged);
            Assert.True(baseline.HasAnyBaseline);
            Assert.True(GhostPlaybackLogic.ResolveSnapshotBaselineActions(baseline).wheelDamaged);
        }

        [Theory]
        [InlineData("False")]
        [InlineData(null)]
        [InlineData("garbage")]
        public void ASnapshotWithAnIntactOrUnreadableWheel_CarriesNoWheelOpinion(string isDamaged)
        {
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(WheelPartNode(isDamaged));

            Assert.True(baseline == null || !baseline.wheelDamaged);
            Assert.False(GhostPlaybackLogic.ResolveSnapshotBaselineActions(baseline).wheelDamaged);
        }

        [Fact]
        public void ABrokenLandingGear_KeepsItsGearPoseAlongsideTheDamage()
        {
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(WheelPartNode("True", withGear: true));

            Assert.True(baseline.wheelDamaged);
            Assert.Equal(true, baseline.gearDeployed);
            var actions = GhostPlaybackLogic.ResolveSnapshotBaselineActions(baseline);
            Assert.True(actions.wheelDamaged);
            Assert.Equal(true, actions.deployableTarget);
        }

        #endregion

        #region Playback applier

        private static GhostPlaybackState StateWithWheel(bool damaged = false)
        {
            return new GhostPlaybackState
            {
                vesselName = "BrokenRover",
                // Empty transform lists: the applier's Unity half is never reached headless.
                wheelDamageInfos = new Dictionary<uint, WheelDamageGhostInfo>
                {
                    { WheelPid, new WheelDamageGhostInfo { partPersistentId = WheelPid, damaged = damaged } },
                },
            };
        }

        [Fact]
        public void TheApplierClassifiesAMissingFamilyAndAMissingPart()
        {
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ApplyWheelDamageStateWithOutcome(
                    new GhostPlaybackState(), WheelPid, damaged: true));
            Assert.Equal(GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ApplyWheelDamageStateWithOutcome(
                    StateWithWheel(), OtherPid, damaged: true));
        }

        [Fact]
        public void BreakAndRepair_FlipTheTrackedFlag_AndGateTheWheelSpin()
        {
            GhostPlaybackState state = StateWithWheel();
            Assert.False(GhostPlaybackLogic.IsGhostWheelDamaged(state, WheelPid));

            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyWheelDamageStateWithOutcome(state, WheelPid, damaged: true));
            Assert.True(state.wheelDamageInfos[WheelPid].damaged);
            Assert.True(GhostPlaybackLogic.IsGhostWheelDamaged(state, WheelPid));

            // Absolute-state: a second break (snapshot baseline + start seed) stays broken.
            Assert.True(GhostPlaybackLogic.ApplyWheelDamageState(state, WheelPid, damaged: true));
            Assert.True(GhostPlaybackLogic.IsGhostWheelDamaged(state, WheelPid));

            Assert.True(GhostPlaybackLogic.ApplyWheelDamageState(state, WheelPid, damaged: false));
            Assert.False(GhostPlaybackLogic.IsGhostWheelDamaged(state, WheelPid));
            Assert.False(GhostPlaybackLogic.IsGhostWheelDamaged(null, WheelPid));
            Assert.False(GhostPlaybackLogic.IsGhostWheelDamaged(state, OtherPid));
        }

        [Fact]
        public void ABrokenWheelSnapshotBaseline_SpawnsTheGhostBroken_AndSaysSoInTheSummary()
        {
            GhostPlaybackState state = StateWithWheel();
            state.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                { WheelPid, new SnapshotPartBaseline { wheelDamaged = true } },
            };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.True(state.wheelDamageInfos[WheelPid].damaged);
            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied")
                && l.Contains("brokenWheels=1"));
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_KeysTheWheelSwapsByPid()
        {
            var state = new GhostPlaybackState { vesselName = "Rover" };
            var result = new GhostBuildResult
            {
                wheelDamageInfos = new List<WheelDamageGhostInfo>
                {
                    new WheelDamageGhostInfo { partPersistentId = WheelPid },
                    new WheelDamageGhostInfo { partPersistentId = OtherPid },
                },
            };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result);

            Assert.Equal(2, state.wheelDamageInfos.Count);
            Assert.False(state.wheelDamageInfos[WheelPid].damaged);
        }

        #endregion

        #region Ghost build gate

        [Theory]
        // swappable, damagedNames, damagedRenderedClones, undamagedClones, expected
        [InlineData(true, 1, 1, 1, "Build")]
        [InlineData(true, 1, 2, 0, "Build")]
        // No busted mesh configured: stock's break just hides the intact wheel, and so do we.
        [InlineData(false, 0, 0, 1, "Build")]
        // A busted mesh is configured but no renderer of it cloned (an empty busted transform from
        // the full-hierarchy mirror counts as nothing): never hide the intact wheel for nothing.
        [InlineData(true, 1, 0, 1, "BustedMeshNotCloned")]
        [InlineData(true, 1, 0, 0, "NothingCloned")]
        [InlineData(false, 0, 0, 0, "NothingCloned")]
        [InlineData(false, 1, 1, 1, "NotSwappable")]
        public void ClassifyWheelDamageBuild_TruthTable(
            bool swappable, int damagedNames, int damagedClones, int undamagedClones,
            string expected)
        {
            // The enum is internal, so the theory names it by string.
            Assert.Equal(expected, GhostVisualBuilder.ClassifyWheelDamageBuild(
                swappable, damagedNames, damagedClones, undamagedClones).ToString());
        }

        [Fact]
        public void BothWheelTransformNamesAreReadFromThePartConfig()
        {
            var config = new ConfigNode("PART");
            ConfigNode damage = config.AddNode("MODULE");
            damage.AddValue("name", "ModuleWheelDamage");
            damage.AddValue("damagedTransformName", "bustedwheel");
            damage.AddValue("undamagedTransformName", "wheel");
            ConfigNode other = config.AddNode("MODULE");
            other.AddValue("name", "ModuleWheelBase");
            other.AddValue("damagedTransformName", "not-this-one");

            Assert.Equal(new[] { "bustedwheel" }, GhostVisualBuilder.GetDamagedWheelTransformNames(config));
            Assert.Equal(new[] { "wheel" }, GhostVisualBuilder.GetUndamagedWheelTransformNames(config));
            Assert.Empty(GhostVisualBuilder.GetUndamagedWheelTransformNames(null));
        }

        #endregion
    }
}
