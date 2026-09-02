using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P8 step 1 guards for the ghost part-event applier's per-family log line.
    ///
    /// The gap this closes (todo PART-EVENT-APPLIER-IS-UNLOGGED): the applier emitted
    /// exactly one aggregate line per call, so no log-reading test could tell "the
    /// recorded event moved the ghost" from "the handler early-returned because the
    /// ghost carries nothing for that pid". Six D7 registry cells were unclaimable for
    /// want of a required token.
    ///
    /// WHAT IS AND IS NOT DRIVABLE HERE, measured rather than assumed. A method whose
    /// BODY names a Unity ECall (any Transform / GameObject / Light / Material write)
    /// cannot even be JIT'd under xUnit: it throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c>
    /// on ENTRY, before the first guard runs, so a guard living inside such a body is
    /// unreachable from a headless test no matter which branch it would take. That is
    /// why every family's preconditions live in a <c>Classify*Apply</c> function the
    /// handler CALLS rather than in an early return the handler owns - one code path,
    /// and a testable one.
    ///
    /// Covered below: the tally and the line grammar (every token, InvariantCulture),
    /// and every family's "the ghost carries nothing here" class - exactly the facts the
    /// old code hid. NOT covered: <c>ApplyPartEvents</c> itself (needs a live ghost
    /// GameObject) and the write-reached-a-live-object arms; the in-game
    /// <c>GhostPlayback</c> category owns those.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPartEventApplyLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostPartEventApplyLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        private static GhostPlaybackState EmptyState() => new GhostPlaybackState();

        private static PartEvent Evt(PartEventType type, uint pid, float value = 0f, int midx = 0)
            => new PartEvent
            {
                ut = 100.0,
                partPersistentId = pid,
                eventType = type,
                partName = "test.part",
                value = value,
                moduleIndex = midx,
            };

        private static string RenderOne(
            PartEventType family,
            GhostPartEventSurface surface,
            int recIdx,
            uint pid,
            GhostPartEventOutcome outcome)
        {
            var tally = new GhostPartEventApplyTally();
            tally.Record(family, surface, pid, outcome);
            return Assert.Single(tally.RenderLines(recIdx));
        }

        // ------------------------------------------------------------------
        // The line grammar
        // ------------------------------------------------------------------

        #region Grammar

        [Fact]
        public void TheLineGrammarIsTheSevenTokenShapeAHarnessParserCanSplitOnWhitespace()
        {
            string line = GhostPartEventApplyLog.FormatLine(
                PartEventType.LightOn, GhostPartEventSurface.ColorChanger,
                recIdx: 12, pid: 100000, applied: 0, skipped: 1,
                reason: GhostPartEventOutcome.NoCabinLightEntry);

            Assert.Equal(
                "apply family=LightOn surface=colorchanger rec=12 pid=100000 " +
                "applied=0 skipped=1 reason=no-cabin-light-entry",
                line);

            // Every token is whitespace-free by construction - that is what lets a
            // parser split the line rather than cutting a free-text tail the way
            // ghostlife.py must for GhostRenderTrace's vessel/reason fields.
            string[] tokens = line.Split(' ');
            Assert.Equal(8, tokens.Length);
            Assert.Equal("apply", tokens[0]);
            for (int i = 1; i < tokens.Length; i++)
                Assert.Equal(1, tokens[i].Count(c => c == '='));
        }

        [Fact]
        public void TheFamilyTokenIsTheEnumMemberNameForEveryDefinedMember_SoASpecCanPinItByName()
        {
            foreach (PartEventType family in Enum.GetValues(typeof(PartEventType)))
            {
                string line = RenderOne(
                    family, GhostPartEventSurface.Visibility, 0, 1u,
                    GhostPartEventOutcome.Applied);
                Assert.Contains("family=" + family.ToString() + " ", line);
            }
        }

        [Fact]
        public void EveryOutcomeAndSurfaceHasADistinctKebabToken_AndNoneIsUnknown()
        {
            var outcomeTokens = Enum.GetValues(typeof(GhostPartEventOutcome))
                .Cast<GhostPartEventOutcome>()
                .Select(GhostPartEventApplyLog.OutcomeToken)
                .ToList();
            Assert.DoesNotContain("unknown", outcomeTokens);
            Assert.Equal(outcomeTokens.Count, outcomeTokens.Distinct().Count());

            var surfaceTokens = Enum.GetValues(typeof(GhostPartEventSurface))
                .Cast<GhostPartEventSurface>()
                .Select(GhostPartEventApplyLog.SurfaceToken)
                .ToList();
            Assert.DoesNotContain("unknown", surfaceTokens);
            Assert.Equal(surfaceTokens.Count, surfaceTokens.Distinct().Count());
        }

        [Fact]
        public void OnlyAppliedCountsAsApplied_TheTwoDeliberateNoOpsAreSkipsWithTheirOwnReason()
        {
            // "Nothing moved" must not hide inside applied=N. A duplicate converter
            // activation and a blink-deferred light both DID run their handler and both
            // left the ghost untouched this frame; the reason class says which.
            Assert.True(GhostPartEventApplyLog.CountsAsApplied(GhostPartEventOutcome.Applied));
            foreach (GhostPartEventOutcome outcome in Enum.GetValues(typeof(GhostPartEventOutcome)))
            {
                if (outcome == GhostPartEventOutcome.Applied) continue;
                Assert.False(GhostPartEventApplyLog.CountsAsApplied(outcome));
            }
        }

        [Fact]
        public void TheNumbersAreInvariantFormatted_UnderACommaDecimalCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostPartEventApplyLog.FormatLine(
                    PartEventType.EngineThrottle, GhostPartEventSurface.EngineFx,
                    recIdx: 1234, pid: 4294967295u, applied: 1000, skipped: 2000,
                    reason: GhostPartEventOutcome.Applied);
                Assert.Contains("rec=1234 pid=4294967295 applied=1000 skipped=2000", line);
                Assert.DoesNotContain(".", line.Substring(line.IndexOf("rec=", StringComparison.Ordinal)));
                Assert.DoesNotContain(",", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        #endregion

        // ------------------------------------------------------------------
        // The tally
        // ------------------------------------------------------------------

        #region Tally

        [Fact]
        public void OneLinePerFamilyAndSurface_AggregatingEveryEventInTheBatch()
        {
            var tally = new GhostPartEventApplyTally();
            tally.Record(PartEventType.LightOn, GhostPartEventSurface.Light, 1u,
                GhostPartEventOutcome.Applied);
            tally.Record(PartEventType.LightOn, GhostPartEventSurface.Light, 2u,
                GhostPartEventOutcome.Applied);
            tally.Record(PartEventType.LightOn, GhostPartEventSurface.ColorChanger, 1u,
                GhostPartEventOutcome.NoCabinLightEntry);
            tally.Record(PartEventType.LightOn, GhostPartEventSurface.ColorChanger, 2u,
                GhostPartEventOutcome.NoInfoForPart);
            tally.Record(PartEventType.GearDeployed, GhostPartEventSurface.Deployable, 7u,
                GhostPartEventOutcome.Applied);

            Assert.Equal(3, tally.DistinctLineCount);
            var lines = tally.RenderLines(5);

            Assert.Contains(
                "apply family=LightOn surface=light rec=5 pid=1 applied=2 skipped=0 reason=applied",
                lines);
            // First SKIP wins the pid and the reason slot: a skip is what a reader hunts.
            Assert.Contains(
                "apply family=LightOn surface=colorchanger rec=5 pid=1 applied=0 skipped=2 " +
                "reason=no-cabin-light-entry",
                lines);
            Assert.Contains(
                "apply family=GearDeployed surface=deployable rec=5 pid=7 applied=1 skipped=0 " +
                "reason=applied",
                lines);
        }

        [Fact]
        public void AMixedBatchReportsBothCountsAndKeepsTheFirstSkipsPidAndReason()
        {
            var tally = new GhostPartEventApplyTally();
            tally.Record(PartEventType.CargoBayOpened, GhostPartEventSurface.Deployable, 10u,
                GhostPartEventOutcome.Applied);
            tally.Record(PartEventType.CargoBayOpened, GhostPartEventSurface.Deployable, 11u,
                GhostPartEventOutcome.NoResolvedVisual);
            tally.Record(PartEventType.CargoBayOpened, GhostPartEventSurface.Deployable, 12u,
                GhostPartEventOutcome.NoInfoForPart);

            Assert.Equal(
                "apply family=CargoBayOpened surface=deployable rec=3 pid=11 applied=1 skipped=2 " +
                "reason=no-resolved-visual",
                Assert.Single(tally.RenderLines(3)));
        }

        [Fact]
        public void FlushWritesTheLineThroughParsekLogUnderTheGhostPartEventsSubsystem()
        {
            var tally = new GhostPartEventApplyTally();
            tally.Record(PartEventType.FairingJettisoned, GhostPartEventSurface.Fairing, 42u,
                GhostPartEventOutcome.NoResolvedVisual);
            tally.Flush(9);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][GhostPartEvents]")
                && l.Contains("apply family=FairingJettisoned surface=fairing rec=9 pid=42 "
                    + "applied=0 skipped=1 reason=no-resolved-visual"));
        }

        [Fact]
        public void ATallyThatRecordedNothingEmitsNothing()
        {
            new GhostPartEventApplyTally().Flush(0);
            Assert.DoesNotContain(logLines, l => l.Contains("[GhostPartEvents]"));
        }

        #endregion

        // ------------------------------------------------------------------
        // Per-family outcomes, driven through the real handlers
        // ------------------------------------------------------------------

        #region Engine and RCS FX

        [Fact]
        public void EngineFx_ReportsNoFamilyStateThenNoInfoForPart()
        {
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.EngineThrottle, 100000u, value: 0.5f);

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyEngineEmissionApply(state, evt));
            Assert.Equal(
                "apply family=EngineThrottle surface=engine-fx rec=0 pid=100000 applied=0 " +
                "skipped=1 reason=no-family-state",
                RenderOne(PartEventType.EngineThrottle, GhostPartEventSurface.EngineFx, 0,
                    evt.partPersistentId, GhostPartEventOutcome.NoFamilyState));

            state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyEngineEmissionApply(state, evt));

            // The audio SURFACE is independent of the FX one: a ghost can carry an
            // engine FX entry and no audio entry, and the old single line said nothing
            // about either.
            state.audioInfos = new Dictionary<ulong, AudioGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyEngineAudioApply(state, evt));
            Assert.Equal(
                "apply family=EngineIgnited surface=engine-audio rec=0 pid=100000 applied=0 " +
                "skipped=1 reason=no-info-for-part",
                RenderOne(PartEventType.EngineIgnited, GhostPartEventSurface.EngineAudio, 0,
                    evt.partPersistentId, GhostPartEventOutcome.NoInfoForPart));
            Assert.Equal(
                "apply family=EngineThrottle surface=engine-fx rec=0 pid=100000 applied=0 " +
                "skipped=1 reason=no-info-for-part",
                RenderOne(PartEventType.EngineThrottle, GhostPartEventSurface.EngineFx, 0,
                    evt.partPersistentId, GhostPartEventOutcome.NoInfoForPart));
        }

        [Fact]
        public void RcsFx_ReportsNoFamilyStateThenNoInfoForPart()
        {
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.RCSActivated, 200000u, value: 1f);

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyRcsEmissionApply(state, evt));

            state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyRcsEmissionApply(state, evt));
            Assert.Equal(
                "apply family=RCSActivated surface=rcs-fx rec=4 pid=200000 applied=0 skipped=1 " +
                "reason=no-info-for-part",
                RenderOne(PartEventType.RCSActivated, GhostPartEventSurface.RcsFx, 4,
                    evt.partPersistentId, GhostPartEventOutcome.NoInfoForPart));
        }

        #endregion

        #region Deployables, gear, bays, shrouds

        [Fact]
        public void Deployable_SeparatesNoFamilyStateNoInfoForPartAndAPoseLessInfo()
        {
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.DeployableExtended, 300000u);

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyDeployableApply(state, evt.partPersistentId));

            state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyDeployableApply(state, evt.partPersistentId));

            // An info that exists but resolved NO transforms at build time: the ghost
            // knows about the panel and cannot pose it. That is a render gap, and it
            // used to be indistinguishable from "no panel here at all".
            state.deployableInfos[evt.partPersistentId] =
                new DeployableGhostInfo { partPersistentId = evt.partPersistentId, transforms = null };
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyDeployableApply(state, evt.partPersistentId));
            Assert.Equal(
                "apply family=DeployableExtended surface=deployable rec=2 pid=300000 applied=0 " +
                "skipped=1 reason=no-resolved-visual",
                RenderOne(PartEventType.DeployableExtended, GhostPartEventSurface.Deployable, 2,
                    evt.partPersistentId, GhostPartEventOutcome.NoResolvedVisual));
        }

        [Fact]
        public void DeployableBroken_SeparatesNoFamilyStateFromNoInfoForPart()
        {
            var state = EmptyState();
            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyDeployableBrokenApply(state, 1u));

            state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyDeployableBrokenApply(state, 1u));

            // An info with no break transform still takes the STATE flag - that is the
            // documented contract (the flag gates sun tracking), so it is Applied.
            state.deployableInfos[1u] = new DeployableGhostInfo { partPersistentId = 1u };
            Assert.Equal(
                GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ClassifyDeployableBrokenApply(state, 1u));
        }

        [Fact]
        public void JettisonPanel_SplitsTheOldSingleReturnFalseIntoItsThreeFacts()
        {
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.ShroudJettisoned, 400000u);

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyJettisonPanelApply(state, evt.partPersistentId));

            state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyJettisonPanelApply(state, evt.partPersistentId));

            state.jettisonInfos[evt.partPersistentId] = new JettisonGhostInfo
            {
                partPersistentId = evt.partPersistentId,
                jettisonTransforms = new List<UnityEngine.Transform>(),
            };
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyJettisonPanelApply(state, evt.partPersistentId));
            Assert.Equal(
                "apply family=ShroudJettisoned surface=jettison-panel rec=1 pid=400000 applied=0 " +
                "skipped=1 reason=no-resolved-visual",
                RenderOne(PartEventType.ShroudJettisoned, GhostPartEventSurface.JettisonPanel, 1,
                    evt.partPersistentId, GhostPartEventOutcome.NoResolvedVisual));
        }

        [Fact]
        public void CargoBayCascade_ReportsBothArmsAndOnlyReachesJettisonWhenTheDeployableArmDeclined()
        {
            var state = EmptyState();
            state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
            state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
            PartEvent evt = Evt(PartEventType.CargoBayOpened, 500000u);

            GhostPlaybackLogic.ApplyCargoBayStateWithOutcomes(
                state, evt, open: true, immediate: false,
                out GhostPartEventOutcome deployable,
                out GhostPartEventOutcome jettison,
                out bool jettisonReached);

            Assert.Equal(GhostPartEventOutcome.NoInfoForPart, deployable);
            Assert.True(jettisonReached);
            Assert.Equal(GhostPartEventOutcome.NoInfoForPart, jettison);
        }

        #endregion

        #region Thermal

        [Fact]
        public void Heat_ReportsNoFamilyStateNoInfoForPartAndAnInfoWithNothingToWrite()
        {
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.ThermalAnimationHot, 600000u);

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyHeatApply(state, evt.partPersistentId));

            state.heatInfos = new Dictionary<uint, HeatGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyHeatApply(state, evt.partPersistentId));

            state.heatInfos[evt.partPersistentId] =
                new HeatGhostInfo { partPersistentId = evt.partPersistentId };
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyHeatApply(state, evt.partPersistentId));
            Assert.Equal(
                "apply family=ThermalAnimationMedium surface=heat rec=0 pid=600000 applied=0 " +
                "skipped=1 reason=no-resolved-visual",
                RenderOne(PartEventType.ThermalAnimationMedium, GhostPartEventSurface.Heat, 0,
                    evt.partPersistentId, GhostPartEventOutcome.NoResolvedVisual));
        }

        #endregion

        #region Converter loop (the one family whose APPLIED path is fully headless)

        [Fact]
        public void ConverterLoop_AppliedThenAlreadyInStateThenNoInfoForPart()
        {
            var state = EmptyState();
            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 1u, true, 10.0));

            state.synthesizedMotionInfos = new SynthesizedMotionGhostInfos
            {
                converterLoops = new List<ConverterLoopGhostInfo>(),
            };
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 1u, true, 10.0));

            var loop = new ConverterLoopGhostInfo { partPersistentId = 1u };
            state.synthesizedMotionInfos.converterLoops.Add(loop);

            Assert.Equal(
                GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 1u, true, 10.0));
            Assert.True(loop.active);
            Assert.Equal(10.0, loop.activeSinceUT);

            // The duplicate-activation ignore: the handler RAN and deliberately changed
            // nothing, which must not read as an apply.
            Assert.Equal(
                GhostPartEventOutcome.AlreadyInState,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 1u, true, 99.0));
            Assert.Equal(10.0, loop.activeSinceUT);

            Assert.Equal(
                "apply family=ConverterActivated surface=converter-loop rec=6 pid=1 applied=0 " +
                "skipped=1 reason=already-in-state",
                RenderOne(PartEventType.ConverterActivated, GhostPartEventSurface.ConverterLoop, 6,
                    1u, GhostPartEventOutcome.AlreadyInState));
        }

        #endregion

        #region Lights and the colour changer (SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE)

        [Fact]
        public void ColorChanger_TellsApartTheFourWaysACabinLightCanFailToToggle()
        {
            var state = EmptyState();

            // (1) nothing on the craft uses ModuleColorChanger at all.
            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 7u));

            // (2) the dictionary exists but Pattern-A discovery resolved nothing for
            // this part - the S1.9 showcase reading's hypothesis (a).
            state.colorChangerInfos = new Dictionary<uint, List<ColorChangerGhostInfo>>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 7u));

            // (3) entries exist but every one is Pattern B (reentry char), so a light
            // event has nothing here to toggle - hypothesis (b), and the reason a
            // showcase row can carry colour-changer geometry and still never blink.
            state.colorChangerInfos[7u] = new List<ColorChangerGhostInfo>
            {
                new ColorChangerGhostInfo
                {
                    partPersistentId = 7u,
                    isCabinLight = false,
                    shaderProperty = "_BurnColor",
                    materials = new List<ColorChangerMaterialState>(),
                },
            };
            Assert.Equal(
                GhostPartEventOutcome.NoCabinLightEntry,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 7u));

            // (4) a cabin-light entry exists but carries no live material.
            state.colorChangerInfos[7u].Add(new ColorChangerGhostInfo
            {
                partPersistentId = 7u,
                isCabinLight = true,
                shaderProperty = "_EmissiveColor",
                materials = new List<ColorChangerMaterialState>(),
            });
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 7u));

            Assert.Equal(
                "apply family=LightOn surface=colorchanger rec=12 pid=7 applied=0 skipped=1 " +
                "reason=no-cabin-light-entry",
                RenderOne(PartEventType.LightOn, GhostPartEventSurface.ColorChanger, 12, 7u,
                    GhostPartEventOutcome.NoCabinLightEntry));
        }

        [Fact]
        public void LightPowerEvent_ReportsItsTwoSurfacesIndependently()
        {
            var state = EmptyState();
            state.lightInfos = new Dictionary<uint, LightGhostInfo>();
            state.colorChangerInfos = new Dictionary<uint, List<ColorChangerGhostInfo>>();

            // Two surfaces, two answers. The single boolean this replaces could not say
            // "the lamp is unknown here AND so is the emissive".
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyUnityLightApply(state, 8u));
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 8u));

            // A part with a Light entry whose components all resolved to null is a
            // DIFFERENT fact again, and was equally invisible before.
            state.lightInfos[8u] = new LightGhostInfo
            {
                partPersistentId = 8u,
                lights = new List<UnityEngine.Light>(),
            };
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyUnityLightApply(state, 8u));

            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyUnityLightApply(EmptyState(), 8u));
        }

        [Fact]
        public void LightOnWhileBlinking_IsDeferredToTheDriverRatherThanReportedAsAnApply()
        {
            var state = EmptyState();
            GhostPlaybackLogic.ApplyLightBlinkModeEventWithOutcome(state, 9u, enabled: true, 2f);

            GhostPlaybackLogic.ApplyLightPowerEventWithOutcomes(
                state, 9u, on: true,
                out GhostPartEventOutcome lightOutcome,
                out GhostPartEventOutcome ccOutcome);

            Assert.Equal(GhostPartEventOutcome.DeferredToDriver, lightOutcome);
            Assert.Equal(GhostPartEventOutcome.DeferredToDriver, ccOutcome);
            Assert.Equal(
                "apply family=LightOn surface=light rec=0 pid=9 applied=0 skipped=1 " +
                "reason=deferred-to-driver",
                RenderOne(PartEventType.LightOn, GhostPartEventSurface.Light, 0, 9u,
                    GhostPartEventOutcome.DeferredToDriver));
        }

        [Fact]
        public void BlinkRate_DiscardsANonPositiveRateAndSaysSo()
        {
            var state = EmptyState();
            Assert.Equal(
                GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyLightBlinkRateEventWithOutcome(state, 10u, 3f));
            Assert.Equal(3f, state.lightPlaybackStates[10u].blinkRateHz);

            Assert.Equal(
                GhostPartEventOutcome.AlreadyInState,
                GhostPlaybackLogic.ApplyLightBlinkRateEventWithOutcome(state, 10u, 0f));
            Assert.Equal(3f, state.lightPlaybackStates[10u].blinkRateHz);
        }

        #endregion

        #region Fairing

        [Fact]
        public void Fairing_SplitsTheOldNestedIfIntoThreeNamedFacts()
        {
            var state = EmptyState();
            Assert.Equal(
                GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyFairingJettisonApply(state, 11u));

            state.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
            Assert.Equal(
                GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ClassifyFairingJettisonApply(state, 11u));

            state.fairingInfos[11u] = new FairingGhostInfo { partPersistentId = 11u };
            Assert.Equal(
                GhostPartEventOutcome.NoResolvedVisual,
                GhostPlaybackLogic.ClassifyFairingJettisonApply(state, 11u));
        }

        #endregion

        #region EVA

        [Fact]
        public void Eva_AnEventTypeTheFlagReducerDoesNotModelIsUnhandledRatherThanSilentlyApplied()
        {
            var state = EmptyState();
            Assert.Equal(
                GhostPartEventOutcome.UnhandledEventType,
                GhostPlaybackLogic.ApplyEvaStateWithOutcome(state, PartEventType.EngineIgnited));
            Assert.Equal(
                "apply family=EvaJetpackDeployed surface=eva rec=0 pid=5 applied=0 skipped=1 " +
                "reason=unhandled-event-type",
                RenderOne(PartEventType.EvaJetpackDeployed, GhostPartEventSurface.Eva, 0, 5u,
                    GhostPartEventOutcome.UnhandledEventType));
        }

        #endregion

        // ------------------------------------------------------------------
        // "Do not change apply behaviour", as a test rather than a claim
        // ------------------------------------------------------------------

        #region Wrapper behaviour preservation

        [Fact]
        public void TheOneBoolWrapperThatIsHeadlesslyCallableStillReturnsExactlyWhatItUsedTo()
        {
            // ONLY the converter-loop family can be driven end to end without Unity:
            // every other wrapper's outcome core names a Transform / GameObject / Light
            // ECall in its BODY, which fails at JIT under xUnit before any branch runs.
            // Their preservation rests on the mechanical shape of the change - each
            // historical `return false` became a named reason, each `return true`
            // became Applied, and the wrapper re-derives the old boolean from those -
            // plus the in-game GhostPlayback coverage.
            //
            // The converter case is also the interesting one: the historical bool was
            // TRUE for the duplicate-activation ignore (a loop WAS reached), which the
            // outcome enum now separates out as AlreadyInState without moving the bool.
            var converterState = EmptyState();
            Assert.False(GhostPlaybackLogic.ApplyConverterLoopState(converterState, 20u, true, 1.0));

            converterState.synthesizedMotionInfos = new SynthesizedMotionGhostInfos
            {
                converterLoops = new List<ConverterLoopGhostInfo>(),
            };
            Assert.False(GhostPlaybackLogic.ApplyConverterLoopState(converterState, 20u, true, 1.0));

            converterState.synthesizedMotionInfos.converterLoops.Add(
                new ConverterLoopGhostInfo { partPersistentId = 20u });
            Assert.True(GhostPlaybackLogic.ApplyConverterLoopState(converterState, 20u, true, 1.0));
            Assert.True(GhostPlaybackLogic.ApplyConverterLoopState(converterState, 20u, true, 2.0));
        }

        [Fact]
        public void EveryClassifierAgreesWithItsHandlerOnTheEmptyState()
        {
            // The classifier IS the handler's early return (the handler calls it), so
            // this pins the one thing a reader cannot see from either half alone: that
            // an entirely empty ghost state reports no-family-state for every family
            // rather than a mix of nulls and no-info.
            var state = EmptyState();
            PartEvent evt = Evt(PartEventType.DeployableExtended, 1u);

            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyEngineEmissionApply(state, evt));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyEngineAudioApply(state, evt));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyRcsEmissionApply(state, evt));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyDeployableApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyDeployableBrokenApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyJettisonPanelApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyHeatApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyFairingJettisonApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyUnityLightApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ClassifyColorChangerLightApply(state, 1u));
            Assert.Equal(GhostPartEventOutcome.NoFamilyState,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 1u, true, 0.0));
        }

        #endregion
    }
}
