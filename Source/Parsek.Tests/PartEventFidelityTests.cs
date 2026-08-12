using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless guards for the P8 part-event fidelity wave — the three recorded signals the
    /// 2026-08-09 part-action audit found MISSING (as opposed to wrong):
    ///
    ///   S6. A solar panel / antenna / radiator that BREAKS. The recorder skipped BROKEN
    ///       outright, so a snapped panel replayed intact forever. <see cref="PartEventType.DeployableBroken"/>
    ///       plus the un-hide-on-repair path.
    ///   S7. A running ISRU / drill. The recorder emitted no PartEvent at all for converter
    ///       activity, so a mining base replayed with a dead-still drill.
    ///   S4. An EVA kerbal's jetpack: deploy / stow, sustained thrust, and ragdoll.
    ///
    /// The corresponding WON'T verdicts (the science timeline) are documented in
    /// docs/dev/research/part-action-recording-audit-2026-08-09.md §2, not here — there is no
    /// code to guard for a signal deliberately not recorded.
    /// </summary>
    public class PartEventFidelityTests
    {
        // ------------------------------------------------------------------
        // Step 2 — enum stability
        // ------------------------------------------------------------------

        #region Enum stability

        [Fact]
        public void P8Members_AreExplicitlyNumbered36Through44_AndDoNotDisturbTheExistingTail()
        {
            // The wave's nine members, pinned by VALUE. These land in .prec sidecars as raw
            // ints (TrajectorySidecarBinary writes `(int)eventType`), so a renumber is a
            // silent cross-version data corruption, not a compile error.
            Assert.Equal(36, (int)PartEventType.DeployableBroken);
            Assert.Equal(37, (int)PartEventType.ConverterActivated);
            Assert.Equal(38, (int)PartEventType.ConverterDeactivated);
            Assert.Equal(39, (int)PartEventType.EvaJetpackDeployed);
            Assert.Equal(40, (int)PartEventType.EvaJetpackStowed);
            Assert.Equal(41, (int)PartEventType.EvaJetpackThrustStarted);
            Assert.Equal(42, (int)PartEventType.EvaJetpackThrustStopped);
            Assert.Equal(43, (int)PartEventType.EvaRagdollStarted);
            Assert.Equal(44, (int)PartEventType.EvaRagdollEnded);

            // The pre-P8 tail the append must not have moved.
            Assert.Equal(33, (int)PartEventType.ParachuteSemiDeployed);
            Assert.Equal(34, (int)PartEventType.ThermalAnimationMedium);
            Assert.Equal(35, (int)PartEventType.ParachuteRepacked);

            // 44 is the new highest member: nothing sits above it, so the next wave appends at 45.
            int[] all = Enum.GetValues(typeof(PartEventType)).Cast<int>().ToArray();
            Assert.Equal(44, all.Max());

            // Contiguous 0..44 with no gaps and no duplicates — the property that makes
            // "append at max+1" a safe rule for the next wave.
            Assert.Equal(45, all.Distinct().Count());
            for (int i = 0; i <= 44; i++)
                Assert.True(Enum.IsDefined(typeof(PartEventType), i), $"PartEventType {i} must be defined");
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 2 — the two classifier gates
        // ------------------------------------------------------------------

        #region Classifier gates

        [Fact]
        public void OnlyDeployableBroken_IsAGhostingTrigger_TheOtherEightAreCosmetic()
        {
            // DeployableBroken hides a mesh subtree, which is a geometry change: the chain
            // walker must rebuild the ghost.
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.DeployableBroken));

            // The rest are overlays on a ghost that already exists. A rebuild for any of them
            // would be pure cost — and the cost is per-recording across a whole tree walk.
            foreach (PartEventType cosmetic in new[]
            {
                PartEventType.ConverterActivated,
                PartEventType.ConverterDeactivated,
                PartEventType.EvaJetpackDeployed,
                PartEventType.EvaJetpackStowed,
                PartEventType.EvaJetpackThrustStarted,
                PartEventType.EvaJetpackThrustStopped,
                PartEventType.EvaRagdollStarted,
                PartEventType.EvaRagdollEnded,
            })
            {
                Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(cosmetic),
                    $"{cosmetic} must not force a ghost rebuild");
            }
        }

        [Fact]
        public void OnlyDeployableBroken_PrewarmsAHiddenGhost()
        {
            // Same asymmetry, different consumer: a hidden ghost must be built early enough to
            // apply a subtree hide, but not for state that re-derives itself on the first
            // visible frame (the converter loop phase) or in the prefix replay (the EVA flags).
            Assert.True(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(
                PartEventType.DeployableBroken));

            foreach (PartEventType noPrewarm in new[]
            {
                PartEventType.ConverterActivated,
                PartEventType.ConverterDeactivated,
                PartEventType.EvaJetpackDeployed,
                PartEventType.EvaJetpackStowed,
                PartEventType.EvaJetpackThrustStarted,
                PartEventType.EvaJetpackThrustStopped,
                PartEventType.EvaRagdollStarted,
                PartEventType.EvaRagdollEnded,
            })
            {
                Assert.False(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(noPrewarm),
                    $"{noPrewarm} must not trigger a hidden-ghost prewarm");
            }
        }

        [Fact]
        public void NoP8Member_IsPermanent_BrokenIsReversibleByRepair()
        {
            // The whole reason DeployableBroken is a reversible-family member and not a
            // ForwardPermanentStateEvents type: stock eventRepairExternal takes a BROKEN panel
            // back to RETRACTED (ModuleDeployablePart.DoRepair), so "broken" is a state a
            // recording can leave, not a one-way door like ShroudJettisoned.
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.DeployableBroken));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ConverterActivated));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ConverterDeactivated));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.EvaJetpackDeployed));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.EvaRagdollStarted));
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 3 — S6: the BROKEN deployable edge
        // ------------------------------------------------------------------

        #region S6 recorder truth table

        private const uint PanelPid = 7001u;
        private const string PanelName = "solarPanels5";

        /// <summary>
        /// The full 2x2 of (isBroken, wasBroken), plus the two set side effects that are the
        /// actual bug surface. Each row runs from a fresh pair of sets so a row cannot pass on
        /// state a previous row left behind.
        /// </summary>
        [Theory]
        // isBroken, wasBroken, wasExtended, expected event (null = none)
        [InlineData(false, false, false, null)]          // intact, unchanged
        [InlineData(false, false, true, null)]           // extended, unchanged
        [InlineData(true, true, false, null)]            // already broken, unchanged
        [InlineData(true, false, false, "DeployableBroken")]     // breaks while stowed
        [InlineData(true, false, true, "DeployableBroken")]      // breaks while extended (the usual case)
        [InlineData(false, true, false, "DeployableRetracted")]  // repaired
        public void CheckDeployableBrokenTransition_TruthTable(
            bool isBroken, bool wasBroken, bool wasExtended, string expectedEventName)
        {
            var brokenSet = new HashSet<uint>();
            var extendedSet = new HashSet<uint>();
            if (wasBroken) brokenSet.Add(PanelPid);
            if (wasExtended) extendedSet.Add(PanelPid);

            PartEvent? evt = FlightRecorder.CheckDeployableBrokenTransition(
                PanelPid, PanelName, isBroken, brokenSet, extendedSet, ut: 500.0);

            if (expectedEventName == null)
            {
                Assert.False(evt.HasValue);
                // A no-op edge must not disturb either set.
                Assert.Equal(wasBroken, brokenSet.Contains(PanelPid));
                Assert.Equal(wasExtended, extendedSet.Contains(PanelPid));
                return;
            }

            Assert.True(evt.HasValue);
            Assert.Equal(expectedEventName, evt.Value.eventType.ToString());
            Assert.Equal(PanelPid, evt.Value.partPersistentId);
            Assert.Equal(PanelName, evt.Value.partName);
            Assert.Equal(500.0, evt.Value.ut);
            // Broken membership always follows the live state.
            Assert.Equal(isBroken, brokenSet.Contains(PanelPid));
        }

        [Fact]
        public void BreakingWhileExtended_SilentlyClearsTheExtendedFlag_SoNoSpuriousRetractFollows()
        {
            // Rule 1, and the reason this is not just a third case bolted onto
            // CheckDeployableTransition. A panel breaks WHILE EXTENDED — that is how it
            // normally breaks (overspeed / impact on a deployed array). If the extended flag
            // survived, the very next poll would read "not extended, was extended" and emit
            // DeployableRetracted, so playback would show the panel folding up neatly and only
            // THEN vanishing.
            var brokenSet = new HashSet<uint>();
            var extendedSet = new HashSet<uint> { PanelPid };

            PartEvent? breakEvt = FlightRecorder.CheckDeployableBrokenTransition(
                PanelPid, PanelName, isBroken: true, brokenSet, extendedSet, ut: 500.0);

            Assert.True(breakEvt.HasValue);
            Assert.Equal(PartEventType.DeployableBroken, breakEvt.Value.eventType);
            Assert.DoesNotContain(PanelPid, extendedSet);
            Assert.Contains(PanelPid, brokenSet);

            // The follow-up poll the live loop would do: the ordinary extended/retracted check
            // now sees not-extended AND not-was-extended, so it stays silent.
            PartEvent? followUp = FlightRecorder.CheckDeployableTransition(
                PanelPid, PanelName, isExtended: false, extendedSet, ut: 500.02);
            Assert.False(followUp.HasValue);
        }

        [Fact]
        public void RepairEmitsRetractedExplicitly_AndLeavesTheExtendedFlagClear()
        {
            // Rule 2. Stock DoRepair lands on RETRACTED, and playback needs a positive
            // instruction to un-hide the subtree — "no event" would leave the ghost rendering a
            // missing panel for a part the recording says was repaired.
            var brokenSet = new HashSet<uint> { PanelPid };
            var extendedSet = new HashSet<uint>();

            PartEvent? repairEvt = FlightRecorder.CheckDeployableBrokenTransition(
                PanelPid, PanelName, isBroken: false, brokenSet, extendedSet, ut: 900.0);

            Assert.True(repairEvt.HasValue);
            Assert.Equal(PartEventType.DeployableRetracted, repairEvt.Value.eventType);
            Assert.DoesNotContain(PanelPid, brokenSet);
            // NOT re-added to extendedSet: RETRACTED is the not-extended state, so a later
            // re-deploy is a genuine DeployableExtended edge rather than a suppressed one.
            Assert.DoesNotContain(PanelPid, extendedSet);

            PartEvent? redeploy = FlightRecorder.CheckDeployableTransition(
                PanelPid, PanelName, isExtended: true, extendedSet, ut: 950.0);
            Assert.True(redeploy.HasValue);
            Assert.Equal(PartEventType.DeployableExtended, redeploy.Value.eventType);
        }

        [Fact]
        public void ABreakRepairRedeployCycle_ProducesExactlyThreeEventsInOrder()
        {
            // The end-to-end sequence a mission actually flies, driven through both pure
            // helpers the way the live poll chains them.
            var brokenSet = new HashSet<uint>();
            var extendedSet = new HashSet<uint>();
            var emitted = new List<PartEventType>();

            void Poll(bool broken, bool extended, double ut)
            {
                PartEvent? b = FlightRecorder.CheckDeployableBrokenTransition(
                    PanelPid, PanelName, broken, brokenSet, extendedSet, ut);
                if (b.HasValue) emitted.Add(b.Value.eventType);
                if (broken) return; // the live poll's `if (isBroken) continue;`
                PartEvent? e = FlightRecorder.CheckDeployableTransition(
                    PanelPid, PanelName, extended, extendedSet, ut);
                if (e.HasValue) emitted.Add(e.Value.eventType);
            }

            Poll(broken: false, extended: false, ut: 100);  // stowed on the pad
            Poll(broken: false, extended: true, ut: 200);   // deploy      -> Extended
            Poll(broken: false, extended: true, ut: 210);   // steady
            Poll(broken: true, extended: false, ut: 300);   // snap        -> Broken
            Poll(broken: true, extended: false, ut: 310);   // steady broken
            Poll(broken: false, extended: false, ut: 400);  // EVA repair  -> Retracted
            Poll(broken: false, extended: false, ut: 410);  // steady

            Assert.Equal(new[]
            {
                PartEventType.DeployableExtended,
                PartEventType.DeployableBroken,
                PartEventType.DeployableRetracted,
            }, emitted);
        }

        #endregion

        #region S6 start-of-recording seeding

        [Fact]
        public void ARecordingThatStartsWithABrokenPanel_SeedsDeployableBrokenAtUT0()
        {
            // Without the seed, a ghost built from the prefab renders an INTACT panel and
            // nothing in the recording ever hides it.
            var sets = new PartTrackingSets();
            sets.brokenDeployables.Add(PanelPid);

            var events = PartStateSeeder.EmitSeedEvents(
                sets, new Dictionary<uint, string> { { PanelPid, PanelName } },
                startUT: 1000.0, logTag: "Recorder");

            PartEvent seed = Assert.Single(
                events.Where(e => e.eventType == PartEventType.DeployableBroken));
            Assert.Equal(PanelPid, seed.partPersistentId);
            Assert.Equal(1000.0, seed.ut);
            Assert.Equal(PanelName, seed.partName);

            // A broken panel is not also seeded as extended: the two sets are disjoint, so the
            // ghost gets one unambiguous instruction.
            Assert.Empty(events.Where(e => e.eventType == PartEventType.DeployableExtended));
        }

        [Fact]
        public void ARecordingThatStartsIntact_SeedsNoBrokenEvent()
        {
            var sets = new PartTrackingSets();
            sets.extendedDeployables.Add(PanelPid);

            var events = PartStateSeeder.EmitSeedEvents(
                sets, new Dictionary<uint, string> { { PanelPid, PanelName } },
                startUT: 1000.0, logTag: "Recorder");

            Assert.Empty(events.Where(e => e.eventType == PartEventType.DeployableBroken));
            Assert.Single(events.Where(e => e.eventType == PartEventType.DeployableExtended));
        }

        #endregion

        #region S6 split-seed placement

        [Fact]
        public void ATailAfterABreak_SeedsDeployableBrokenVerbatim_NotRetracted()
        {
            // The parachute-trio property applied to the deployable family: Retracted and Broken
            // are both "not extended" but they render differently, so the reducer must carry the
            // event type VERBATIM. A collapsed-to-Retracted seed would put the tail's ghost's
            // panel back on the craft.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableExtended, partName = PanelName },
                new PartEvent { ut = 200, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableBroken, partName = PanelName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);

            PartEvent seed = Assert.Single(
                seeds.Where(s => s.partPersistentId == PanelPid));
            Assert.Equal(PartEventType.DeployableBroken, seed.eventType);
            Assert.Equal(300.0, seed.ut);
        }

        [Fact]
        public void ATailAfterABreakThenRepair_SeedsRetracted_BecauseTheFamilyCollapsesToItsLastState()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableExtended, partName = PanelName },
                new PartEvent { ut = 200, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableBroken, partName = PanelName },
                new PartEvent { ut = 250, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableRetracted, partName = PanelName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);

            PartEvent seed = Assert.Single(seeds.Where(s => s.partPersistentId == PanelPid));
            Assert.Equal(PartEventType.DeployableRetracted, seed.eventType);
        }

        [Fact]
        public void BrokenAndExtended_ShareFamilyThree_SoOneSeedWinsPerPart()
        {
            // Family membership is what makes the reducer collapse the run to a single opinion
            // (and what makes the boundary dedupe recognise an existing event as covering the
            // seed). A separate family would emit BOTH a Broken and an Extended seed for one
            // pivot, and playback would apply them in list order.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableBroken, partName = PanelName },
                new PartEvent { ut = 150, partPersistentId = PanelPid,
                    eventType = PartEventType.DeployableExtended, partName = PanelName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            Assert.Single(seeds.Where(s => s.partPersistentId == PanelPid));
        }

        #endregion

        #region S6 rails-span reconcile

        private static List<PartEvent> DiffSets(PartTrackingSets before, PartTrackingSets after)
            => PartStateSeeder.EmitDiffEvents(
                before, after,
                new Dictionary<uint, string> { { PanelPid, PanelName } },
                ut: 2000.0, logTag: "BgRecorder");

        [Fact]
        public void APanelThatBreaksAcrossARailsSpan_EmitsBrokenAndNotAlsoRetracted()
        {
            // The precedence rule. On the diff path a break shows up as TWO set changes at once
            // (leaves extendedDeployables, arrives in brokenDeployables). Diffing them
            // independently would emit both DeployableRetracted and DeployableBroken, and
            // playback would fold the panel shut before hiding it — the same artefact rule 1
            // prevents on the live path.
            var before = new PartTrackingSets();
            before.extendedDeployables.Add(PanelPid);

            var after = new PartTrackingSets();
            after.brokenDeployables.Add(PanelPid);

            var events = DiffSets(before, after);

            Assert.Single(events);
            Assert.Equal(PartEventType.DeployableBroken, events[0].eventType);
            Assert.Equal(PanelPid, events[0].partPersistentId);
            Assert.Equal(2000.0, events[0].ut);
        }

        [Fact]
        public void APanelRepairedAcrossARailsSpan_EmitsRetracted()
        {
            var before = new PartTrackingSets();
            before.brokenDeployables.Add(PanelPid);
            var after = new PartTrackingSets();

            var events = DiffSets(before, after);

            Assert.Single(events);
            Assert.Equal(PartEventType.DeployableRetracted, events[0].eventType);
        }

        [Fact]
        public void APanelRepairedAndRedeployedAcrossARailsSpan_EmitsBothEvents()
        {
            // The case the arrival side deliberately has NO carve-out for: broken before,
            // extended after, means the kerbal repaired it and then deployed it again. Both are
            // real, and swallowing the Extended would leave the ghost's panel folded.
            var before = new PartTrackingSets();
            before.brokenDeployables.Add(PanelPid);
            var after = new PartTrackingSets();
            after.extendedDeployables.Add(PanelPid);

            var events = DiffSets(before, after);

            Assert.Equal(2, events.Count);
            Assert.Contains(events, e => e.eventType == PartEventType.DeployableRetracted);
            Assert.Contains(events, e => e.eventType == PartEventType.DeployableExtended);
        }

        [Fact]
        public void APanelBrokenOnBothSidesOfARailsSpan_EmitsNothing()
        {
            var before = new PartTrackingSets();
            before.brokenDeployables.Add(PanelPid);
            var after = new PartTrackingSets();
            after.brokenDeployables.Add(PanelPid);

            Assert.Empty(DiffSets(before, after));
        }

        [Fact]
        public void AnOrdinaryRetractAcrossARailsSpan_StillEmitsRetracted()
        {
            // Guard against the carve-out over-reaching: with no broken pid anywhere, the
            // extended diff must behave exactly as it did before P8.
            var before = new PartTrackingSets();
            before.extendedDeployables.Add(PanelPid);
            var after = new PartTrackingSets();

            var events = DiffSets(before, after);

            Assert.Single(events);
            Assert.Equal(PartEventType.DeployableRetracted, events[0].eventType);
        }

        [Fact]
        public void CloningTrackingSets_CarriesTheBrokenSetByVALUE()
        {
            // The rails reconciler holds `before` across a span while the originals keep
            // mutating, so a shallow copy would diff the live set against itself and report
            // nothing ever changed.
            var source = new PartTrackingSets();
            source.brokenDeployables.Add(PanelPid);

            PartTrackingSets clone = PartStateSeeder.ClonePartTrackingSets(source);
            Assert.Contains(PanelPid, clone.brokenDeployables);

            source.brokenDeployables.Clear();
            Assert.Contains(PanelPid, clone.brokenDeployables);
            Assert.NotSame(source.brokenDeployables, clone.brokenDeployables);
        }

        #endregion

        #region S6 snapshot baseline

        private static ConfigNode PartNodeWithDeployState(string deployState)
        {
            var partNode = new ConfigNode("PART");
            ConfigNode module = partNode.AddNode("MODULE");
            module.AddValue("name", "ModuleDeployableSolarPanel");
            module.AddValue("deployState", deployState);
            return partNode;
        }

        [Fact]
        public void ASnapshotWhoseDeployStateIsBROKEN_FillsTheBrokenBaselineAndNoPoseOpinion()
        {
            // The slot the pre-P8 comment explicitly deferred. A station snapshotted after
            // losing a panel must spawn its ghost with the subtree hidden.
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(PartNodeWithDeployState("BROKEN"));

            Assert.NotNull(baseline);
            Assert.True(baseline.deployableBroken);
            // No stowed/deployed opinion: broken is off that axis, and giving it one would pose
            // the panel as well as hiding it.
            Assert.False(baseline.deployableExtended.HasValue);
            Assert.True(baseline.HasAnyBaseline);
        }

        [Theory]
        [InlineData("EXTENDED", true)]
        [InlineData("RETRACTED", false)]
        public void ASnapshotWithAnOrdinaryDeployState_IsUnchangedByTheBrokenSlot(
            string deployState, bool expectedExtended)
        {
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(PartNodeWithDeployState(deployState));

            Assert.NotNull(baseline);
            Assert.False(baseline.deployableBroken);
            Assert.Equal(expectedExtended, baseline.deployableExtended);
        }

        [Theory]
        [InlineData("EXTENDING")]
        [InlineData("RETRACTING")]
        public void ASnapshotCaughtMidTravel_StillCarriesNoDeployableOpinionAtAll(string deployState)
        {
            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(PartNodeWithDeployState(deployState));

            // Nothing readable on this node -> no baseline object at all, exactly as before P8.
            Assert.Null(baseline);
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 4 — S6 playback (the pure half; the appliers touch Transforms
        // and are proven by the H37 in-game cells)
        // ------------------------------------------------------------------

        #region S6 snapshot baseline resolution

        [Fact]
        public void ABrokenSnapshotBaseline_ResolvesToTheBrokenActionAndNoPoseAction()
        {
            var baseline = new SnapshotPartBaseline { deployableBroken = true };

            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(baseline);

            Assert.True(actions.deployableBroken);
            // No pose: the two drive different ghost surfaces and the panel is not on the
            // stowed<->deployed axis at all.
            Assert.False(actions.deployableTarget.HasValue);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void AnOrdinaryDeployableBaseline_ResolvesWithTheBrokenActionOff()
        {
            // Regression guard: the new flag must default false for every pre-P8 baseline shape,
            // or every ghost would spawn with its panels hidden.
            var extended = new SnapshotPartBaseline { deployableExtended = true };
            Assert.False(GhostPlaybackLogic.ResolveSnapshotBaselineActions(extended).deployableBroken);
            Assert.True(GhostPlaybackLogic.ResolveSnapshotBaselineActions(extended).deployableTarget);

            var gear = new SnapshotPartBaseline { gearDeployed = false };
            Assert.False(GhostPlaybackLogic.ResolveSnapshotBaselineActions(gear).deployableBroken);

            var empty = new SnapshotPartBaseline();
            Assert.False(GhostPlaybackLogic.ResolveSnapshotBaselineActions(empty).deployableBroken);
        }

        [Fact]
        public void ABrokenBaseline_SurvivesTheParseToResolveRoundTrip()
        {
            // The two halves joined: a real snapshot MODULE node through the parser and out of the
            // resolver as an actionable hide. This is the path a station recorded after losing an
            // array actually takes.
            SnapshotPartBaseline parsed =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(PartNodeWithDeployState("BROKEN"));
            Assert.NotNull(parsed);

            GhostPlaybackLogic.SnapshotBaselineActions actions =
                GhostPlaybackLogic.ResolveSnapshotBaselineActions(parsed);
            Assert.True(actions.deployableBroken);
        }

        #endregion

        #region S6 playback applier guards

        // WHY THERE IS NO HEADLESS CELL FOR ApplyDeployableBrokenState ITSELF, measured rather
        // than assumed. It was written, and it threw:
        //
        //   System.Security.SecurityException : ECall methods must be packaged into a system module
        //     at Parsek.GhostPlaybackLogic.ApplyDeployableBrokenState
        //
        // The applier compares `info.breakSubtreeRoot != null`, and UnityEngine.Object's overloaded
        // == routes through a native ECall that xUnit cannot host. That is the same wall every
        // Unity-touching applier in this file's neighbours hits (see the SnapshotBaseline category's
        // rationale: "the appliers that actually MOVE something are Unity-coupled"). Using
        // ReferenceEquals to dodge it would be worse than the gap — it would let the applier write
        // to a DESTROYED transform, which is exactly what the Unity null check exists to prevent.
        //
        // So the division of proof is deliberate: the pure DECISIONS are pinned here (the parser,
        // the resolver, the gate's report), and the ACT of hiding and re-showing a real subtree is
        // pinned by the H37 in-game cell, which has a live ghost to hide.

        [Fact]
        public void TheSunTrackingGateReportsTheBrokenFlag_SoARedNamesItsOwnCause()
        {
            // The gate's diagnostic string is what an in-game red pastes into its failure message.
            // A panel held because it is BROKEN must be distinguishable from one held because it
            // is stowed, or the next investigation starts by re-flying.
            var info = new DeployableGhostInfo
            {
                partPersistentId = PanelPid,
                transforms = new List<DeployableTransformState>(),
                currentDeployed = true,
                deployFraction = 1f,
                breakSubtreeHidden = true,
            };
            var state = new GhostPlaybackState
            {
                deployableInfos = new Dictionary<uint, DeployableGhostInfo> { { PanelPid, info } }
            };

            string described = GhostPlaybackLogic.DescribeDeployableGateForPart(state, PanelPid);
            Assert.Contains("breakSubtreeHidden=True", described);

            info.breakSubtreeHidden = false;
            Assert.Contains("breakSubtreeHidden=False",
                GhostPlaybackLogic.DescribeDeployableGateForPart(state, PanelPid));
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 5 — S7: the converter running loop
        // ------------------------------------------------------------------

        #region S7 recorder truth table

        private const uint DrillPid = 8001u;
        private const string DrillName = "RadialDrill";

        [Theory]
        // isActive, wasActive, expected event (null = none)
        [InlineData(false, false, null)]
        [InlineData(true, true, null)]
        [InlineData(true, false, "ConverterActivated")]
        [InlineData(false, true, "ConverterDeactivated")]
        public void CheckConverterTransition_TruthTable(
            bool isActive, bool wasActive, string expectedEventName)
        {
            var activeSet = new HashSet<uint>();
            if (wasActive) activeSet.Add(DrillPid);

            PartEvent? evt = FlightRecorder.CheckConverterTransition(
                DrillPid, DrillName, isActive, activeSet, ut: 700.0);

            if (expectedEventName == null)
            {
                Assert.False(evt.HasValue);
                Assert.Equal(wasActive, activeSet.Contains(DrillPid));
                return;
            }

            Assert.True(evt.HasValue);
            Assert.Equal(expectedEventName, evt.Value.eventType.ToString());
            Assert.Equal(DrillPid, evt.Value.partPersistentId);
            Assert.Equal(700.0, evt.Value.ut);
            Assert.Equal(isActive, activeSet.Contains(DrillPid));
        }

        [Fact]
        public void AMiningSessionProducesExactlyTwoEvents_TheStatedBudget()
        {
            // The budget claim in the enum's comment, held to. Converter state is a player toggle,
            // not a continuous signal, so a whole session that polls hundreds of times must still
            // cost exactly one pair.
            var activeSet = new HashSet<uint>();
            var emitted = new List<PartEventType>();

            void Poll(bool active, double ut)
            {
                PartEvent? e = FlightRecorder.CheckConverterTransition(
                    DrillPid, DrillName, active, activeSet, ut);
                if (e.HasValue) emitted.Add(e.Value.eventType);
            }

            for (int i = 0; i < 50; i++) Poll(active: false, ut: 100 + i);
            for (int i = 0; i < 500; i++) Poll(active: true, ut: 200 + i);
            for (int i = 0; i < 50; i++) Poll(active: false, ut: 800 + i);

            Assert.Equal(new[]
            {
                PartEventType.ConverterActivated,
                PartEventType.ConverterDeactivated,
            }, emitted);
        }

        [Fact]
        public void ARecordingThatStartsWithTheDrillRunning_SeedsConverterActivatedAtUT0()
        {
            // Without the seed the loop would start at the recording's first TOGGLE, which for a
            // base that has been mining for hours may never come at all.
            var sets = new PartTrackingSets();
            sets.activeConverterParts.Add(DrillPid);

            var events = PartStateSeeder.EmitSeedEvents(
                sets, new Dictionary<uint, string> { { DrillPid, DrillName } },
                startUT: 5000.0, logTag: "Recorder");

            PartEvent seed = Assert.Single(
                events.Where(e => e.eventType == PartEventType.ConverterActivated));
            Assert.Equal(DrillPid, seed.partPersistentId);
            Assert.Equal(5000.0, seed.ut);
        }

        [Fact]
        public void ADrillToggledAcrossARailsSpan_ReconcilesInBothDirections()
        {
            var on = new PartTrackingSets();
            on.activeConverterParts.Add(DrillPid);
            var off = new PartTrackingSets();

            var started = PartStateSeeder.EmitDiffEvents(
                off, on, new Dictionary<uint, string> { { DrillPid, DrillName } }, 2000.0, "BgRecorder");
            Assert.Single(started);
            Assert.Equal(PartEventType.ConverterActivated, started[0].eventType);

            var stopped = PartStateSeeder.EmitDiffEvents(
                on, off, new Dictionary<uint, string> { { DrillPid, DrillName } }, 2000.0, "BgRecorder");
            Assert.Single(stopped);
            Assert.Equal(PartEventType.ConverterDeactivated, stopped[0].eventType);

            // Running on both sides of the span is not a change.
            Assert.Empty(PartStateSeeder.EmitDiffEvents(
                on, PartStateSeeder.ClonePartTrackingSets(on),
                new Dictionary<uint, string>(), 2000.0, "BgRecorder"));
        }

        [Fact]
        public void ATailAfterTheDrillWasSwitchedOff_SeedsTheDeactivation()
        {
            // The inactive direction is NOT redundant here, unlike thermal-cold: nothing else stops
            // the loop, so a tail with no deactivation seed would spin a drill the recording says
            // is idle for its whole span.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = DrillPid,
                    eventType = PartEventType.ConverterActivated, partName = DrillName },
                new PartEvent { ut = 200, partPersistentId = DrillPid,
                    eventType = PartEventType.ConverterDeactivated, partName = DrillName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            PartEvent seed = Assert.Single(seeds.Where(s => s.partPersistentId == DrillPid));
            Assert.Equal(PartEventType.ConverterDeactivated, seed.eventType);
            Assert.Equal(300.0, seed.ut);
        }

        [Fact]
        public void ATailWhileTheDrillIsStillRunning_SeedsTheActivation()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = DrillPid,
                    eventType = PartEventType.ConverterActivated, partName = DrillName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            PartEvent seed = Assert.Single(seeds.Where(s => s.partPersistentId == DrillPid));
            Assert.Equal(PartEventType.ConverterActivated, seed.eventType);
            // The seed's UT is the SPLIT, not the original activation. The loop restarts its phase
            // at the cut, which is a sub-cycle discontinuity in the drill's rotation and nothing a
            // player can perceive - and the alternative (carrying the original UT past a cut whose
            // tail may not even contain it) is worse.
            Assert.Equal(300.0, seed.ut);
        }

        [Fact]
        public void ConverterEventsAreTheirOwnSeedFamily_NotFoldedIntoTheDeployableOne()
        {
            // Family 11 exists so a drill part that ALSO has a deployable (RadialDrill has both a
            // ModuleAnimationGroup deploy and a converter) gets one seed per family rather than one
            // opinion overwriting the other.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = DrillPid,
                    eventType = PartEventType.DeployableExtended, partName = DrillName },
                new PartEvent { ut = 150, partPersistentId = DrillPid,
                    eventType = PartEventType.ConverterActivated, partName = DrillName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            var forDrill = seeds.Where(s => s.partPersistentId == DrillPid).ToList();

            Assert.Equal(2, forDrill.Count);
            Assert.Contains(forDrill, s => s.eventType == PartEventType.DeployableExtended);
            Assert.Contains(forDrill, s => s.eventType == PartEventType.ConverterActivated);
        }

        #endregion

        #region S7 loop phase arithmetic

        [Fact]
        public void TheLoopPhaseIsAPureFunctionOfElapsedRecordedTime()
        {
            const float clip = 4f;

            Assert.Equal(0f, GhostPlaybackLogic.ComputeConverterLoopPhase(100.0, 100.0, clip));
            Assert.Equal(0.25, GhostPlaybackLogic.ComputeConverterLoopPhase(101.0, 100.0, clip), 5);
            Assert.Equal(0.5, GhostPlaybackLogic.ComputeConverterLoopPhase(102.0, 100.0, clip), 5);

            // WRAPPING is the point: at exactly one clip length the loop is back where it started,
            // and at 2.5 clips it is halfway round again.
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeConverterLoopPhase(104.0, 100.0, clip), 5);
            Assert.Equal(0.5, GhostPlaybackLogic.ComputeConverterLoopPhase(110.0, 100.0, clip), 5);
        }

        [Fact]
        public void ReplayingTheSameRecordedMomentAlwaysGivesTheSamePhase()
        {
            // The S2 argument, restated for the loop: no wall clock is involved, so a scrub back to
            // a moment renders the pose that moment always rendered. Two calls separated by any
            // amount of real time agree, and so do a forward pass and a rewound one.
            const float clip = 3.5f;
            float first = GhostPlaybackLogic.ComputeConverterLoopPhase(1234.75, 1000.0, clip);
            float again = GhostPlaybackLogic.ComputeConverterLoopPhase(1234.75, 1000.0, clip);
            Assert.Equal(first, again);

            // And an exact whole number of cycles later is the same pose again.
            Assert.Equal((double)first, (double)GhostPlaybackLogic.ComputeConverterLoopPhase(
                1234.75 + clip * 100, 1000.0, clip), 4);
        }

        [Theory]
        [InlineData(0f)]        // a clip length that would divide by zero
        [InlineData(-1f)]       // nonsense from a corrupt cache
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void AnUnusableClipLengthParksTheLoopAtPhaseZero(float clip)
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeConverterLoopPhase(500.0, 100.0, clip));
        }

        [Fact]
        public void APlaybackUTBeforeTheLoopStarted_ParksAtPhaseZeroRatherThanGoingNegative()
        {
            // Reachable on a scrub backwards past the activation before the event index rewinds.
            Assert.Equal(0f, GhostPlaybackLogic.ComputeConverterLoopPhase(90.0, 100.0, 4f));
        }

        [Fact]
        public void ThePhaseIsAlwaysAValidIndexIntoTheSampledPoses()
        {
            // The specific crash this guards: the drive does phases[(int)(phase * N)], so a phase
            // that ever reached exactly 1.0 would index off the end of the array.
            const int n = PartEventFidelityTests.PhaseCount;
            foreach (double ut in new[] { 100.0, 100.0001, 103.9999, 104.0, 104.0001, 1e6, 1e9 })
            {
                float phase = GhostPlaybackLogic.ComputeConverterLoopPhase(ut, 100.0, 4f);
                Assert.InRange(phase, 0f, 0.9999999f);

                GhostPlaybackLogic.ResolveConverterLoopBlend(
                    phase, n, out int from, out int to, out float blend);
                Assert.InRange(from, 0, n - 1);
                Assert.InRange(to, 0, n - 1);
                Assert.InRange(blend, 0f, 1f);
            }
        }

        internal const int PhaseCount = GhostVisualBuilder.ConverterLoopPhaseCount;

        [Fact]
        public void TheBlendPairWrapsFromTheLastSampledPhaseBackToTheFirst()
        {
            // The cyclic property the sampler depends on: it deliberately does NOT store a
            // duplicate endpoint pose (phase i is sampled at i/N, not i/(N-1)), so the LAST phase
            // must blend toward phase 0 or the loop would stall for one interval every cycle.
            GhostPlaybackLogic.ResolveConverterLoopBlend(
                0.999f, 12, out int from, out int to, out float blend);
            Assert.Equal(11, from);
            Assert.Equal(0, to);
            Assert.InRange(blend, 0.9f, 1f);

            GhostPlaybackLogic.ResolveConverterLoopBlend(
                0f, 12, out from, out to, out blend);
            Assert.Equal(0, from);
            Assert.Equal(1, to);
            Assert.Equal(0f, blend);

            GhostPlaybackLogic.ResolveConverterLoopBlend(
                0.5f, 12, out from, out to, out blend);
            Assert.Equal(6, from);
            Assert.Equal(7, to);
            Assert.Equal(0.0, (double)blend, 4);
        }

        [Fact]
        public void ASinglePhaseOrNoPhases_DegradesToAStillPose()
        {
            GhostPlaybackLogic.ResolveConverterLoopBlend(0.7f, 1, out int from, out int to, out float blend);
            Assert.Equal(0, from);
            Assert.Equal(0, to);
            Assert.Equal(0f, blend);

            GhostPlaybackLogic.ResolveConverterLoopBlend(0.7f, 0, out from, out to, out blend);
            Assert.Equal(0, from);
            Assert.Equal(0, to);
            Assert.Equal(0f, blend);
        }

        [Fact]
        public void TwelvePhasesIsThePinnedSampleCount()
        {
            // Pinned because it is a storage-vs-smoothness trade the design principle asks to be
            // deliberate about, and because the wrap cells above are written against it.
            Assert.Equal(12, GhostVisualBuilder.ConverterLoopPhaseCount);
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 6 — S4: the EVA jetpack and ragdoll
        // ------------------------------------------------------------------

        #region S4 recorder edges

        private const uint KerbalPid = 9001u;
        private const string KerbalName = "kerbalEVA";

        [Theory]
        [InlineData(false, false, null)]
        [InlineData(true, true, null)]
        [InlineData(true, false, "EvaJetpackDeployed")]
        [InlineData(false, true, "EvaJetpackStowed")]
        public void CheckEvaJetpackTransition_TruthTable(
            bool deployed, bool wasDeployed, string expectedEventName)
        {
            var set = new HashSet<uint>();
            if (wasDeployed) set.Add(KerbalPid);

            PartEvent? evt = FlightRecorder.CheckEvaJetpackTransition(
                KerbalPid, KerbalName, deployed, set, ut: 300.0);

            if (expectedEventName == null)
            {
                Assert.False(evt.HasValue);
                Assert.Equal(wasDeployed, set.Contains(KerbalPid));
                return;
            }

            Assert.True(evt.HasValue);
            Assert.Equal(expectedEventName, evt.Value.eventType.ToString());
            Assert.Equal(300.0, evt.Value.ut);
            Assert.Equal(deployed, set.Contains(KerbalPid));
        }

        [Theory]
        [InlineData(false, false, null)]
        [InlineData(true, true, null)]
        [InlineData(true, false, "EvaRagdollStarted")]
        [InlineData(false, true, "EvaRagdollEnded")]
        public void CheckEvaRagdollTransition_TruthTable(
            bool ragdoll, bool wasRagdoll, string expectedEventName)
        {
            var set = new HashSet<uint>();
            if (wasRagdoll) set.Add(KerbalPid);

            PartEvent? evt = FlightRecorder.CheckEvaRagdollTransition(
                KerbalPid, KerbalName, ragdoll, set, ut: 400.0);

            if (expectedEventName == null)
            {
                Assert.False(evt.HasValue);
                return;
            }

            Assert.True(evt.HasValue);
            Assert.Equal(expectedEventName, evt.Value.eventType.ToString());
            Assert.Equal(ragdoll, set.Contains(KerbalPid));
        }

        #endregion

        #region S4 thrust debounce

        private static List<PartEvent> RunThrustFrames(params bool[] thrustingPerFrame)
        {
            var counts = new Dictionary<uint, int>();
            var thrusting = new HashSet<uint>();
            var events = new List<PartEvent>();
            for (int i = 0; i < thrustingPerFrame.Length; i++)
            {
                FlightRecorder.ProcessEvaThrustDebounce(
                    KerbalPid, KerbalName, thrustingPerFrame[i], ut: 1000.0 + i,
                    counts, thrusting, events);
            }
            return events;
        }

        private static bool[] Frames(bool value, int count)
        {
            var a = new bool[count];
            for (int i = 0; i < count; i++) a[i] = value;
            return a;
        }

        [Fact]
        public void TheThrustDebounceReusesTheRcsThreshold()
        {
            // Pinned deliberately: JetpackIsThrusting flickers for the same reason RCS does (both
            // are recomputed per FixedUpdate from a fuel-flow comparison), so a SECOND independent
            // number would be a second thing to keep in step for no reason.
            Assert.Equal(8, FlightRecorder.RcsDebounceFrameThreshold);
        }

        [Fact]
        public void ASingleFrameTap_EmitsNothingAtAll()
        {
            // The whole point of the debounce. A kerbal nudging himself toward a hatch taps
            // translation for a frame or two; recorded raw that would be a start/stop pair each time.
            Assert.Empty(RunThrustFrames(true, false));
            Assert.Empty(RunThrustFrames(true, true, false));
        }

        [Fact]
        public void ATapJustShortOfTheThreshold_EmitsNothing_AndLeavesNoStaleState()
        {
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var frames = new List<bool>(Frames(true, threshold - 1)) { false };
            Assert.Empty(RunThrustFrames(frames.ToArray()));
        }

        [Fact]
        public void ASustainedBurst_EmitsExactlyOneStartAndOneStop_TheStatedBudget()
        {
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var frames = new List<bool>(Frames(true, threshold + 40)) { false };

            var events = RunThrustFrames(frames.ToArray());

            Assert.Equal(2, events.Count);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, events[0].eventType);
            Assert.Equal(PartEventType.EvaJetpackThrustStopped, events[1].eventType);
        }

        [Fact]
        public void TheStartEventFiresOnTheThresholdFrame_AndCarriesValueOne()
        {
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var events = RunThrustFrames(Frames(true, threshold));

            PartEvent start = Assert.Single(events);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, start.eventType);
            // The threshold frame's own UT: frames start at 1000 and the Nth frame is 1000+(N-1).
            Assert.Equal(1000.0 + (threshold - 1), start.ut);
            // Binary, not power-valued: a jetpack has no throttle a player can set.
            Assert.Equal(1f, start.value);
        }

        [Fact]
        public void PastTheThreshold_NoFurtherEventsAreEmittedWhileThrustContinues()
        {
            // Unlike RCS there is no continuous power to re-report, so a long burn must not
            // dribble events - which is what a copy-paste of the RCS path would have done.
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var events = RunThrustFrames(Frames(true, threshold + 500));
            Assert.Single(events);
        }

        [Fact]
        public void TwoSeparateSustainedBursts_EmitTwoPairs()
        {
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var frames = new List<bool>();
            frames.AddRange(Frames(true, threshold + 5));
            frames.AddRange(Frames(false, 10));
            frames.AddRange(Frames(true, threshold + 5));
            frames.Add(false);

            var events = RunThrustFrames(frames.ToArray());

            Assert.Equal(4, events.Count);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, events[0].eventType);
            Assert.Equal(PartEventType.EvaJetpackThrustStopped, events[1].eventType);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, events[2].eventType);
            Assert.Equal(PartEventType.EvaJetpackThrustStopped, events[3].eventType);
        }

        [Fact]
        public void AFilteredTapAfterASustainedBurst_DoesNotEmitAnUnpairedStop()
        {
            // The state-leak this guards: if the filtered-tap branch forgot to clear membership, the
            // next stop would emit without a matching start and playback would see a stop for a
            // plume it never lit.
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var frames = new List<bool>();
            frames.AddRange(Frames(true, threshold + 3));
            frames.Add(false);                    // -> real stop
            frames.AddRange(Frames(true, 2));     // a tap, filtered
            frames.Add(false);
            frames.Add(false);

            var events = RunThrustFrames(frames.ToArray());

            Assert.Equal(2, events.Count);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, events[0].eventType);
            Assert.Equal(PartEventType.EvaJetpackThrustStopped, events[1].eventType);
        }

        [Fact]
        public void ARecordingThatEndsMidBurst_LeavesTheStartWithNoStop_WhichIsCorrect()
        {
            // A burst still running when the recording ends emits only the start. The playback loop
            // reset and the ghost teardown both stop the plume, so there is nothing left lit.
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var events = RunThrustFrames(Frames(true, threshold + 20));
            Assert.Single(events);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, events[0].eventType);
        }

        #endregion

        #region S4 seeding and split seeds

        [Fact]
        public void ARecordingThatStartsMidSpacewalk_SeedsThePackAndTheRagdoll()
        {
            var sets = new PartTrackingSets();
            sets.jetpackDeployedParts.Add(KerbalPid);
            sets.ragdollParts.Add(KerbalPid);

            var events = PartStateSeeder.EmitSeedEvents(
                sets, new Dictionary<uint, string> { { KerbalPid, KerbalName } },
                startUT: 4000.0, logTag: "Recorder");

            Assert.Single(events.Where(e => e.eventType == PartEventType.EvaJetpackDeployed));
            Assert.Single(events.Where(e => e.eventType == PartEventType.EvaRagdollStarted));
            // THRUST is never seeded: it is a momentary input, and a kerbal caught mid-burst has
            // the debounce window ahead of him anyway.
            Assert.Empty(events.Where(e => e.eventType == PartEventType.EvaJetpackThrustStarted));
        }

        [Fact]
        public void TheJetpackPoseAndRagdollReconcileAcrossARailsSpan_ButThrustDoesNot()
        {
            var before = new PartTrackingSets();
            var after = new PartTrackingSets();
            after.jetpackDeployedParts.Add(KerbalPid);
            after.ragdollParts.Add(KerbalPid);

            var events = PartStateSeeder.EmitDiffEvents(
                before, after, new Dictionary<uint, string> { { KerbalPid, KerbalName } },
                2000.0, "BgRecorder");

            Assert.Equal(2, events.Count);
            Assert.Contains(events, e => e.eventType == PartEventType.EvaJetpackDeployed);
            Assert.Contains(events, e => e.eventType == PartEventType.EvaRagdollStarted);
            Assert.DoesNotContain(events, e => e.eventType == PartEventType.EvaJetpackThrustStarted);

            // And the reverse direction, so a kerbal who stowed and got up is reconciled too.
            var backAgain = PartStateSeeder.EmitDiffEvents(
                after, before, new Dictionary<uint, string> { { KerbalPid, KerbalName } },
                2000.0, "BgRecorder");
            Assert.Equal(2, backAgain.Count);
            Assert.Contains(backAgain, e => e.eventType == PartEventType.EvaJetpackStowed);
            Assert.Contains(backAgain, e => e.eventType == PartEventType.EvaRagdollEnded);
        }

        [Fact]
        public void TheJetpackPoseAndRagdollSeedAcrossASplit_InBothDirections()
        {
            var deployedAndDown = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaJetpackDeployed, partName = KerbalName },
                new PartEvent { ut = 120, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaRagdollStarted, partName = KerbalName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(deployedAndDown, splitUT: 300.0);
            Assert.Contains(seeds, s => s.eventType == PartEventType.EvaJetpackDeployed);
            Assert.Contains(seeds, s => s.eventType == PartEventType.EvaRagdollStarted);

            var stowedAndUp = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaJetpackDeployed, partName = KerbalName },
                new PartEvent { ut = 150, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaJetpackStowed, partName = KerbalName },
                new PartEvent { ut = 160, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaRagdollStarted, partName = KerbalName },
                new PartEvent { ut = 200, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaRagdollEnded, partName = KerbalName },
            };

            var seeds2 = RecordingOptimizer.BuildTransientStateSeeds(stowedAndUp, splitUT: 300.0);
            Assert.Contains(seeds2, s => s.eventType == PartEventType.EvaJetpackStowed);
            Assert.Contains(seeds2, s => s.eventType == PartEventType.EvaRagdollEnded);
        }

        [Fact]
        public void TheThrustPairIsNotASplitSeedFamily_ItFollowsTheRcsRule()
        {
            // "Not thrusting" is the prefab default, and a burst still running at a split is a
            // momentary input the tail's own frames re-establish. Seeding it would assert a state
            // the tail has no reason to inherit.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaJetpackThrustStarted, partName = KerbalName, value = 1f },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            Assert.Empty(seeds.Where(s =>
                s.eventType == PartEventType.EvaJetpackThrustStarted
                || s.eventType == PartEventType.EvaJetpackThrustStopped));
        }

        [Fact]
        public void TheJetpackAndRagdollPairsAreSeparateSeedFamilies()
        {
            // Families 12 and 13. One kerbal can be BOTH pack-out and ragdolled, and folding them
            // into one family would let one state's seed overwrite the other's.
            var events = new List<PartEvent>
            {
                new PartEvent { ut = 100, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaJetpackDeployed, partName = KerbalName },
                new PartEvent { ut = 120, partPersistentId = KerbalPid,
                    eventType = PartEventType.EvaRagdollStarted, partName = KerbalName },
            };

            var seeds = RecordingOptimizer.BuildTransientStateSeeds(events, splitUT: 300.0);
            Assert.Equal(2, seeds.Count(s => s.partPersistentId == KerbalPid));
        }

        #endregion

        #region S4 plume gate

        [Theory]
        // deployed, thrusting, ragdoll, plume?
        [InlineData(true, true, false, true)]    // the only case that emits
        [InlineData(false, true, false, false)]  // thrusting with the pack stowed — cannot happen physically
        [InlineData(true, false, false, false)]  // pack out, idle
        [InlineData(true, true, true, false)]    // tumbling: stock cuts thrust
        [InlineData(false, false, false, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, false, true, false)]
        public void TheJetpackPlumeGate_IsTheFullThreeFlagTruthTable(
            bool deployed, bool thrusting, bool ragdoll, bool expected)
        {
            Assert.Equal(expected,
                GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(deployed, thrusting, ragdoll));
        }

        [Fact]
        public void RagdollSuppressesThePlumeEvenWhileTheRecordingSaysThrusting()
        {
            // The one place the ragdoll events earn their keep VISUALLY, given the pose itself is
            // deliberately never replayed. Stock cuts thrust when the FSM enters ragdoll, and the
            // recorder's two flags are read independently, so this combination does occur on disk.
            Assert.True(GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                deployed: true, thrusting: true, ragdoll: false));
            Assert.False(GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                deployed: true, thrusting: true, ragdoll: true));
        }

        // TryUpdateEvaFlags is the headless-reachable half. ApplyEvaState wraps it and then
        // reconciles the particle system, which compares a GameObject against null and so routes
        // through a UnityEngine.Object ECall xUnit cannot host — the same wall the S6 appliers hit.
        // The plume actually lighting and going out is pinned by the H37 in-game cell.

        [Fact]
        public void TryUpdateEvaFlags_TracksAllSixEdgesOnThePlaybackState()
        {
            var state = new GhostPlaybackState();

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaJetpackDeployed));
            Assert.True(state.evaJetpackDeployed);

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaJetpackThrustStarted));
            Assert.True(state.evaJetpackThrusting);

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaRagdollStarted));
            Assert.True(state.evaRagdoll);

            // With all three set, the gate is CLOSED by the ragdoll — the combination that exists
            // on disk and must not light a plume.
            Assert.False(GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll));

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaRagdollEnded));
            Assert.False(state.evaRagdoll);
            // He is up again, pack out, still thrusting: NOW it emits.
            Assert.True(GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll));

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaJetpackThrustStopped));
            Assert.False(state.evaJetpackThrusting);

            Assert.True(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EvaJetpackStowed));
            Assert.False(state.evaJetpackDeployed);

            Assert.False(GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll));
        }

        [Fact]
        public void AKerbalStillThrustingAtARecordingBoundary_GetsATerminalStop()
        {
            // REVIEW FIX. Nothing else can close an EVA thrust pair. A kerbal switched away from
            // mid-burst stops being polled by the flight recorder before the false-thrust edge
            // lands, and the BACKGROUND recorder has no thrust wrapper at all (a background kerbal
            // receives no input, so the edge can never fire there). An RCS pair left open at the
            // same handoff is closed by the BG recorder's own CheckRcsState; a thrust pair would
            // stay open, leaving the ghost's plume lit for the rest of the recording AND re-lit on
            // every loop cycle.
            var thrusting = new HashSet<uint> { KerbalPid };

            var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                activeEngineKeys: new HashSet<ulong>(),
                activeRcsKeys: new HashSet<ulong>(),
                activeRoboticKeys: new HashSet<ulong>(),
                lastRoboticPosition: new Dictionary<ulong, float>(),
                finalUT: 7777.0,
                logTag: "Recorder",
                thrustingJetpackParts: thrusting);

            PartEvent stop = Assert.Single(terminals);
            Assert.Equal(PartEventType.EvaJetpackThrustStopped, stop.eventType);
            Assert.Equal(KerbalPid, stop.partPersistentId);
            Assert.Equal(7777.0, stop.ut);

            // The emit does NOT mutate the set — that is the engine/RCS/robotics convention, and
            // the callers own the lifetime. Both halves of why are the two cells below.
            Assert.Contains(KerbalPid, thrusting);
        }

        [Fact]
        public void TheRailsPathClearIsWhatMakesASecondTerminalEmitANoOp_NotARemoveOnEmit()
        {
            // REVIEW FIX (F1). Double-emit safety comes from the same mechanism engines use: the
            // rails call site (EmitTerminalEventsAndClearActiveState) clears every tracking set
            // right after the emit returns, so the `Count > 0` gate inside the emit sees an empty
            // set the second time round. The STOP site does not clear — by design, see the next
            // cell — and never emits twice over a live set without an intervening resume.
            var thrusting = new HashSet<uint> { KerbalPid };

            var first = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 7777.0, "Recorder", thrusting);
            Assert.Single(first);

            thrusting.Clear();   // what the rails site does immediately afterwards

            Assert.Empty(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 8888.0, "Recorder", thrusting));
        }

        [Fact]
        public void AFalseAlarmStopMidBurst_ResumesTracking_AndClosesAtTheREALThrustEnd()
        {
            // REVIEW FIX (F1). The route a remove-on-emit broke. The STOP path deliberately leaves
            // the tracking sets intact so ResumeAfterFalseAlarm can unwind an abandoned
            // chain-boundary stop and keep tracking a burn that never actually ended. With the pid
            // removed by the emit, the resumed poll's real thrust-end edge hit
            // `thrustingParts.Remove(pid) == false` in ProcessEvaThrustDebounce and emitted
            // NOTHING — the pair stayed open to recording end, which is precisely the failure the
            // terminal emit exists to prevent.
            int threshold = FlightRecorder.RcsDebounceFrameThreshold;
            var counts = new Dictionary<uint, int>();
            var thrusting = new HashSet<uint>();
            var partEvents = new List<PartEvent>();

            // A burst starts and is sustained past the debounce threshold.
            for (int i = 0; i < threshold + 5; i++)
            {
                FlightRecorder.ProcessEvaThrustDebounce(
                    KerbalPid, KerbalName, true, ut: 1000.0 + i, counts, thrusting, partEvents);
            }
            Assert.Single(partEvents);
            Assert.Equal(PartEventType.EvaJetpackThrustStarted, partEvents[0].eventType);

            // A chain-boundary stop mid-burst: terminal close appended, tracking sets left intact.
            var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), finalUT: 2000.0, logTag: "Recorder",
                thrustingJetpackParts: thrusting);
            partEvents.AddRange(terminals);
            Assert.Equal(2, partEvents.Count);

            // The split turns out to be debris only: ResumeAfterFalseAlarm content-removes exactly
            // the terminals the stop stashed (#287).
            Assert.Equal(1, FlightRecorder.RemoveTerminalsFromList(partEvents, terminals));
            Assert.Single(partEvents);

            // Tracking survived the unwind, so the burn is still open and still ours.
            Assert.Contains(KerbalPid, thrusting);

            // More thrusting frames, then the REAL thrust-end edge.
            for (int i = 0; i < 20; i++)
            {
                FlightRecorder.ProcessEvaThrustDebounce(
                    KerbalPid, KerbalName, true, ut: 2100.0 + i, counts, thrusting, partEvents);
            }
            FlightRecorder.ProcessEvaThrustDebounce(
                KerbalPid, KerbalName, false, ut: 2200.0, counts, thrusting, partEvents);

            // Exactly one close, at the REAL edge rather than the abandoned stop's UT.
            PartEvent stop = Assert.Single(
                partEvents.Where(e => e.eventType == PartEventType.EvaJetpackThrustStopped));
            Assert.Equal(2200.0, stop.ut);
            Assert.Equal(2, partEvents.Count);
            Assert.Empty(thrusting);
        }

        [Fact]
        public void TheTerminalEmitIsUnchangedForANullOrEmptyThrustSet()
        {
            // The parameter is optional so ~20 existing call sites and test cells are untouched;
            // this pins that a null set really is the old behaviour.
            Assert.Empty(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 100.0, "Recorder"));

            Assert.Empty(FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 100.0, "Recorder",
                thrustingJetpackParts: new HashSet<uint>()));
        }

        // ------------------------------------------------------------------
        // The plume reconcile classifier — added after the 2026-08-12 H37
        // measuring flight found the inactive-hierarchy no-op
        // ------------------------------------------------------------------

        [Fact]
        public void AnInactiveHierarchyDefersThePlumeRatherThanBuildingOrFailingIt()
        {
            // THE FLIGHT FINDING, pinned. Unity's ParticleSystem.Play() on a ghost that is not
            // activeInHierarchy is a SILENT no-op — no throw, isPlaying stays false — and a ghost is
            // inactive for the whole of its spawn-time prefix replay (the build ends with
            // SetActive(false); activation happens later, per frame). So a ghost whose playback
            // cursor lands inside a thrust burst consumed its EvaJetpackThrustStarted against an
            // inactive hierarchy and, the reconcile being event-driven only, never tried again.
            Assert.Equal(
                GhostPlaybackLogic.EvaPlumeReconcileAction.DeferInactiveHierarchy,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(
                    wanted: true, hasPlume: false, unavailable: false,
                    hierarchyActive: false, isPlaying: false));

            // Deferral WINS over both the build and the unavailable flag. Over the build because
            // allocating a system that cannot start is pure cost for a ghost that may never be
            // shown; over `unavailable` because an inactive ghost is a retry-later, and recording it
            // as a permanent failure would mean the plume never appears for that ghost even once it
            // becomes visible.
            Assert.Equal(
                GhostPlaybackLogic.EvaPlumeReconcileAction.DeferInactiveHierarchy,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(
                    wanted: true, hasPlume: true, unavailable: true,
                    hierarchyActive: false, isPlaying: false));
        }

        [Fact]
        public void ThePlumeReconcileClassifierCoversEveryReachableCombination()
        {
            // --- gate SHUT: only ever stop-or-nothing, and never a build ---
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.None,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(false, false, false, true, false));
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.None,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(false, true, false, true, false));
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.Stop,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(false, true, false, true, true));
            // A shut gate does NOT consult the hierarchy: stopping an emitting system is valid
            // whatever the ghost's visibility, and a hidden ghost must not be left emitting.
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.Stop,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(false, true, false, false, true));

            // --- gate OPEN, hierarchy ACTIVE ---
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.Build,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(true, false, false, true, false));
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.Play,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(true, true, false, true, false));
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.AlreadyPlaying,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(true, true, false, true, true));
            Assert.Equal(GhostPlaybackLogic.EvaPlumeReconcileAction.Unavailable,
                GhostPlaybackLogic.ClassifyEvaPlumeReconcile(true, false, true, true, false));

            // Every one of the 32 combinations resolves to a DEFINED action (no silent
            // fall-through), which is what makes the wrapper's switch exhaustive, plus the two
            // invariants that carry the fix.
            foreach (bool wanted in new[] { false, true })
            foreach (bool hasPlume in new[] { false, true })
            foreach (bool unavailable in new[] { false, true })
            foreach (bool active in new[] { false, true })
            foreach (bool playing in new[] { false, true })
            {
                GhostPlaybackLogic.EvaPlumeReconcileAction a =
                    GhostPlaybackLogic.ClassifyEvaPlumeReconcile(
                        wanted, hasPlume, unavailable, active, playing);
                Assert.True(Enum.IsDefined(typeof(GhostPlaybackLogic.EvaPlumeReconcileAction), a));

                // A build is only ever proposed when there is nothing to build on AND the ghost can
                // actually show it — the property that keeps the laziness claim true.
                if (a == GhostPlaybackLogic.EvaPlumeReconcileAction.Build)
                    Assert.True(wanted && !hasPlume && !unavailable && active);

                // Neither Build nor Play is ever proposed on an inactive hierarchy: that no-op is
                // the whole reason this classifier exists.
                if (a == GhostPlaybackLogic.EvaPlumeReconcileAction.Build
                    || a == GhostPlaybackLogic.EvaPlumeReconcileAction.Play)
                {
                    Assert.True(active);
                }
            }
        }

        [Fact]
        public void ThePerFrameSelfHealCostsOneBoolReadForANonThrustingGhost()
        {
            // The gate that keeps a per-frame hook affordable: essentially every ghost in a scene is
            // not an EVA kerbal mid-burst, and must pay one field read and nothing else. A null
            // state is also a no-op, since the hook runs from the frame path.
            var idle = new GhostPlaybackState();
            GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(idle);
            Assert.Null(idle.evaJetpackPlumeInfo);
            Assert.False(idle.evaJetpackPlumeUnavailable);

            GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(null);
        }

        [Fact]
        public void TryUpdateEvaFlags_IgnoresNonEvaEventTypesAndANullState()
        {
            var state = new GhostPlaybackState();
            Assert.False(GhostPlaybackLogic.TryUpdateEvaFlags(state, PartEventType.EngineIgnited));
            Assert.False(state.evaJetpackDeployed);
            Assert.False(state.evaJetpackThrusting);
            Assert.False(state.evaRagdoll);

            Assert.False(GhostPlaybackLogic.TryUpdateEvaFlags(null, PartEventType.EvaJetpackDeployed));
        }

        #endregion
    }
}
