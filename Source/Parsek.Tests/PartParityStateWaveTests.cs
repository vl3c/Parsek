using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure decisions of the live state engine (<see cref="PartParityStateWave"/>,
    /// design-part-ghost-parity.md Appendix D.3 to D.5): the state wave's slot rule and its
    /// one-bucket composition, the worst-case admission, the refused press, the filler node,
    /// the stepped primes, the state flags, the event rows and the stowed pairing id.
    /// </summary>
    public class PartParityStateWaveTests
    {
        private static PartParityCaseKey Key(string part, string family, string state, string context = "alone")
            => new PartParityCaseKey
            {
                Part = part, Source = "Squad", State = state, Context = context,
                StateOption = family == null ? null : new PartParityStateOption { Family = family, State = state },
            };

        // ----- slots -----

        [Fact]
        public void PlanStateWave_UsesEveryOtherSlot_UpToFour()
        {
            Assert.Equal(new[] { 60.0, -60.0, 140.0, -140.0 }, PartParityStateWave.PlanStateWave(9).ToArray());
            Assert.Equal(new[] { 60.0, -60.0, 140.0 }, PartParityStateWave.PlanStateWave(3).ToArray());
            Assert.Empty(PartParityStateWave.PlanStateWave(0));
            Assert.Empty(PartParityStateWave.PlanStateWave(-2));
            // 80 m apart and at least 60 m from the host.
            Assert.Equal(80.0, PartParityStateWave.StateSlotPositionsM[1] - PartParityStateWave.StateSlotPositionsM[0]);
        }

        // ----- composition -----

        [Fact]
        public void WaveKind_IsStowedOrTheBucket()
        {
            Assert.Equal(PartParityStateWave.KindStowed, PartParityStateWave.WaveKind(Key("a", null, "stowed")));
            Assert.Equal(PartParityStateWave.KindStowed, PartParityStateWave.WaveKind(null));
            Assert.Equal("state:Long", PartParityStateWave.WaveKind(Key("a", PartParityStateRules.FamilyDeployable, "deployed")));
            Assert.Equal("state:Quick", PartParityStateWave.WaveKind(Key("a", PartParityStateRules.FamilyLight, "lit")));
            Assert.Equal("state:Air", PartParityStateWave.WaveKind(Key("a", PartParityStateRules.FamilyParachute, "semi", "air")));
            Assert.Equal(PartParityStateBucket.Quick, PartParityStateWave.BucketOfKind("state:Quick"));
            Assert.Equal(PartParityStateBucket.Long, PartParityStateWave.BucketOfKind("state:bogus"));
        }

        [Fact]
        public void PickWave_StowedIsTodaysLeadingRun()
        {
            var kinds = new[] { "stowed", "stowed", "stowed", "state:Long" };
            var solo = new[] { false, false, false, false };
            Assert.Equal(new[] { 0, 1, 2 }, PartParityStateWave.PickWave(kinds, solo, 0, 8, 4).ToArray());
            Assert.Equal(new[] { 0, 1 }, PartParityStateWave.PickWave(kinds, solo, 0, 2, 4).ToArray());
            // A solo case flies alone; a solo case ends a stowed run.
            Assert.Equal(new[] { 1 }, PartParityStateWave.PickWave(kinds, new[] { false, true, false, false }, 1, 8, 4).ToArray());
            Assert.Equal(new[] { 0 }, PartParityStateWave.PickWave(kinds, new[] { false, true, false, false }, 0, 8, 4).ToArray());
        }

        [Fact]
        public void PickWave_StateHoldsOneBucket_SkippingOthers()
        {
            var kinds = new[] { "state:Long", "state:Quick", "state:Long", "state:Long", "state:Long", "state:Long" };
            var solo = new[] { false, false, false, true, false, false };
            Assert.Equal(new[] { 0, 2, 4, 5 }, PartParityStateWave.PickWave(kinds, solo, 0, 8, 4).ToArray());
            Assert.Equal(new[] { 1 }, PartParityStateWave.PickWave(kinds, solo, 1, 8, 4).ToArray());
            Assert.Empty(PartParityStateWave.PickWave(kinds, solo, 9, 8, 4));
        }

        [Fact]
        public void BringToCursor_IsAStablePartition()
        {
            var q = new[] { "a", "b", "c", "d", "e", "f" };
            Assert.Equal(new[] { "a", "c", "e", "b", "d", "f" },
                PartParityStateWave.BringToCursor(q, 1, new[] { 2, 4 }).ToArray());
            // The identity for a contiguous pick (every stowed wave).
            Assert.Equal(q, PartParityStateWave.BringToCursor(q, 0, new[] { 0, 1, 2 }).ToArray());
        }

        // ----- admission -----

        [Fact]
        public void AdmitWave_StowedReadsTheLastWave_StateTheWorstCase()
        {
            Assert.True(PartParityStateWave.AdmitWave("stowed", 0.0, 480, 1000.0, 8, 0.0));
            Assert.True(PartParityStateWave.AdmitWave("stowed", 470.0, 480, 5.0, 8, 0.0));
            Assert.False(PartParityStateWave.AdmitWave("stowed", 470.0, 480, 20.0, 8, 0.0));
            // Long, 4 cases, a sun tracker: 71.7 s.
            Assert.True(PartParityStateWave.AdmitWave("state:Long", 408.0, 480, 1.0, 4, PartParityStateRules.SunTrackerCapSeconds));
            Assert.False(PartParityStateWave.AdmitWave("state:Long", 409.0, 480, 1.0, 4, PartParityStateRules.SunTrackerCapSeconds));
            // The airbrake and a light's lens fade raise a quick wave's settle to their 5 s cap.
            Assert.True(PartParityStateWave.AdmitWave("state:Quick", 447.0, 480, 1.0, 4, 0.0, new[] { "jettison" }));
            Assert.False(PartParityStateWave.AdmitWave("state:Quick", 447.0, 480, 1.0, 4, 0.0, new[] { "light" }));
            Assert.True(PartParityStateWave.AdmitWave("state:Quick", 443.0, 480, 1.0, 4, 0.0, new[] { "light" }));
            // A folding radiator's 62.5 s tracking cap: 109.2 s for a long wave of four.
            Assert.True(PartParityStateWave.AdmitWave("state:Long", 370.0, 480, 1.0, 4, PartParityStateRules.SunTrackerCapSecondsFor(0.1)));
            Assert.False(PartParityStateWave.AdmitWave("state:Long", 371.0, 480, 1.0, 4, PartParityStateRules.SunTrackerCapSecondsFor(0.1)));
            Assert.False(PartParityStateWave.AdmitWave("state:Quick", 447.0, 480, 1.0, 4, 0.0, new[] { "airbrake" }));
            Assert.True(PartParityStateWave.AdmitWave("state:Quick", 0.0, 480, 1.0, 4, 0.0, new[] { "airbrake" }));
            // The first wave of a step starts whatever the box (planning makes elapsed > 0).
            Assert.False(PartParityStateWave.AdmitWave("state:Long", 4.0, 60, 0.0, 4, PartParityStateRules.SunTrackerCapSeconds));
            Assert.True(PartParityStateWave.AdmitWave("state:Long", 4.0, 60, 0.0, 4, PartParityStateRules.SunTrackerCapSeconds, null, firstWaveOfStep: true));
            // The stowed rule never reads the flag.
            Assert.False(PartParityStateWave.AdmitWave("stowed", 470.0, 480, 20.0, 8, 0.0, null, firstWaveOfStep: true));
        }

        // ----- press -----

        [Fact]
        public void PressRefused_OnlyWhenNothingMoved()
        {
            Assert.True(PartParityStateWave.PressRefused("a=1;", "a=1;", false, false));
            Assert.False(PartParityStateWave.PressRefused("a=1;", "a=2;", false, false));
            Assert.False(PartParityStateWave.PressRefused("a=1;", "a=1;", true, false));
            Assert.False(PartParityStateWave.PressRefused("a=1;", "a=1;", false, true));
            Assert.True(PartParityStateWave.PressRefused(null, string.Empty, false, false));
        }

        [Theory]
        [InlineData(0.9375, "bottom")]  // a top node (turboFanSize2) meets the filler's bottom
        [InlineData(-0.9375, "top")]    // a bottom node (liquidEngine2) meets the filler's top
        [InlineData(0.0, "top")]
        public void FillerAttachNode_IsTheOppositeNode(double y, string expected)
            => Assert.Equal(expected, PartParityStateWave.FillerAttachNode(y));

        // ----- stowed pose -----

        [Theory]
        [InlineData(1f, 1f)]
        [InlineData(0f, 0f)]
        [InlineData(1.5f, 1f)]
        [InlineData(-1f, 0f)]
        [InlineData(float.NaN, 0f)]
        public void CargoBayClosedAnimTime_IsTheClampedClosedPosition(float closed, float expected)
            => Assert.Equal(expected, PartParityStateWave.CargoBayClosedAnimTime(closed));

        [Theory]
        [InlineData(0f, true)]
        [InlineData(1f, false)]
        public void AnimSwitchAt_MatchesTheEndTheModuleRestsAt(float animTime, bool expected)
            => Assert.Equal(expected, PartParityStateWave.AnimSwitchAt(animTime));

        [Fact]
        public void CrashedStateProof_CarriesTheCaseStateWord()
            => Assert.Equal("deployed-m3", PartParityStateWave.CrashedStateProof(
                new PartParityStateOption { Family = "animate-generic", State = "deployed" }, "deployed-m3").State);

        [Fact]
        public void GearStowedState_IsTheRetractedStateTheGearClassifierReads()
        {
            FlightRecorder.ClassifyGearState(PartParityStateWave.GearStowedStateString, out bool deployed, out bool retracted);
            Assert.False(deployed);
            Assert.True(retracted);
        }

        // ----- primes -----

        [Fact]
        public void EventRoutePrimes_LastEventPlusClipPlusOne_SteppedForSunTrackers()
        {
            Assert.Equal(new[] { 1005.0 }, PartParityStateWave.EventRoutePrimeUTs(1000.0, new[] { 1001.0, 1002.0 }, 2.0, false).ToArray());
            Assert.Equal(new[] { 1004.0 }, PartParityStateWave.EventRoutePrimeUTs(1000.0, null, 3.0, false).ToArray());
            Assert.Equal(new[] { 1001.0 }, PartParityStateWave.EventRoutePrimeUTs(1000.0, new[] { 999.0, double.NaN }, double.NaN, false).ToArray());
            List<double> sun = PartParityStateWave.EventRoutePrimeUTs(1000.0, new[] { 1002.0 }, 3.0, true);
            Assert.Equal(11, sun.Count);
            Assert.Equal(1006.0, sun[0]);
            Assert.Equal(1016.0, sun[10]);
            for (int i = 1; i < sun.Count; i++)
                Assert.Equal(PartParityStateWave.PrimeStepSeconds, sun[i] - sun[i - 1], 9);
            // Each step at the synthesized-motion sample cap, so no prime re-seeds.
            Assert.True(PartParityStateWave.PrimeStepSeconds <= Parsek.GhostPlaybackLogic.MaxSynthSampleSeconds);
        }

        [Fact]
        public void StartRoutePrimes_FirstFrameThenSteps()
        {
            Assert.Equal(new[] { 50.0 }, PartParityStateWave.StartRoutePrimeUTs(50.0, false).ToArray());
            Assert.Equal(Enumerable.Range(0, 11).Select(k => 50.0 + k).ToArray(),
                PartParityStateWave.StartRoutePrimeUTs(50.0, true).ToArray());
        }

        // ----- flags -----

        [Fact]
        public void StateFlags_CleanCaseWritesNone()
        {
            Assert.Empty(PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = true, Reached = true, EventsRecorded = 1, GhostEvent = "applied", GhostStart = "applied",
                StartFirstFrameAtTarget = true, SunFirstFrameDeltaDeg = 0.5,
            }));
            Assert.Empty(PartParityStateWave.StateFlags(null));
        }

        [Fact]
        public void StateFlags_EveryFlagInOrder()
        {
            List<string> f = PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = true, Reached = true, EventsRecorded = 0, GhostEvent = "no-info", GhostStart = "not-reached",
                StartFirstFrameAtTarget = false, SunFirstFrameDeltaDeg = 37.25, StillTracking = true,
                ContextHidden = 3, DebrisHidden = 2,
            });
            Assert.Equal(new[]
            {
                "state-not-recorded", "state-not-modelled:event:no-info", "state-not-modelled:start:not-reached",
                "start-pose-not-first-frame", "sun-tracker-first-frame:37.3", "still-tracking",
                "context-hidden:3", "jettison-debris-hidden:2",
            }, f.ToArray());
        }

        [Fact]
        public void StateFlags_ANotPressedContextCaseIsNotUnrecorded()
        {
            List<string> f = PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = false, Reached = true, EventsRecorded = 0, GhostEvent = "applied", ContextHidden = 1,
            });
            Assert.Equal(new[] { "context-hidden:1" }, f.ToArray());
        }

        // ----- rows and ids -----

        [Fact]
        public void ProofEvents_CarryTypeUtPidMidx()
        {
            var rows = PartParityStateWave.ProofEvents(new List<PartEvent>
            {
                new PartEvent { eventType = PartEventType.DeployableExtended, ut = 12.5, partPersistentId = 7, moduleIndex = 0 },
            });
            Assert.Single(rows);
            Assert.Equal("DeployableExtended", rows[0].Type);
            Assert.Equal(12.5, rows[0].Ut);
            Assert.Equal(7u, rows[0].Pid);
            Assert.Empty(PartParityStateWave.ProofEvents(null));
        }

        [Fact]
        public void StowedCaseId_PairsAStateCaseWithItsStowedAlone()
        {
            PartParityCaseKey state = Key("longAntenna", PartParityStateRules.FamilyDeployable, "deployed");
            state.Skin = "default";
            var stowed = new PartParityCaseKey { Part = "longAntenna", Source = "Squad", Skin = "default" };
            Assert.Equal(TestCommandPartParitySweep.CaseId(stowed), PartParityStateWave.StowedCaseId(state));
            Assert.Null(PartParityStateWave.StowedCaseId(stowed));
            Assert.Null(PartParityStateWave.StowedCaseId(null));
            // The stacked stowed shroud pairs with the alone stowed case too.
            PartParityCaseKey stacked = Key("liquidEngine2", PartParityStateRules.FamilyJettison, "stowed", "stacked");
            Assert.EndsWith(TestCommandPartParitySweep.CaseId(new PartParityCaseKey { Part = "liquidEngine2", Source = "Squad" }),
                PartParityStateWave.StowedCaseId(stacked));
        }

        [Fact]
        public void CrashedStateProof_NamesTheOption_NeverReached()
        {
            Assert.Null(PartParityStateWave.CrashedStateProof(null));
            PartParityStateProof p = PartParityStateWave.CrashedStateProof(new PartParityStateOption
            {
                Family = "jettison", Module = "ModuleJettison", ModuleIndex = 3, State = "jettisoned",
                Action = "Jettison", NodeNames = new List<string> { "bottom" },
            });
            Assert.Equal("jettison", p.Family);
            Assert.Equal("jettisoned", p.State);
            Assert.False(p.Reached);
            Assert.Null(p.ActionResult);
            Assert.Equal("bottom", Assert.Single(p.Nodes).Node);
            Assert.Equal(PartParityStateWave.FillerPartName, p.Nodes[0].Part);
        }

        [Fact]
        public void DescribeState_NamesEveryField()
        {
            string line = PartParityStateWave.DescribeState("x--default--deployed--12345678",
                new PartParityStateProof { Family = "deployable", Module = "ModuleDeployableAntenna", ModuleIndex = 1,
                    State = "deployed", Action = "Extend", ActionResult = "invoked", Reached = true, WaitS = 3.25,
                    EndpointField = "deployState", EndpointValue = "EXTENDED", GhostEvent = "applied", GhostStart = "applied" },
                "ok");
            Assert.Equal("case x--default--deployed--12345678: state family=deployable module=ModuleDeployableAntenna midx=1 " +
                "state=deployed action=Extend result=invoked reached=true waitS=3.25 endpoint=deployState=EXTENDED events=- " +
                "ghostEvent=applied ghostStart=applied status=ok", line);
            Assert.Contains("family=<none>", PartParityStateWave.DescribeState(null, null, null));
        }
    }
}
