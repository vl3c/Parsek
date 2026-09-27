using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure core of the ghost CommNet relay / control point (design 15.6). Expected powers are
    /// derived by hand from stock CommNetVessel.UpdateComm (KSP 1.12.5); the arithmetic is in
    /// each test's comment. Stock part values: RA-2 (RelayAntenna5) RELAY 2e9 combinable,
    /// exponent default 0.75; probe core INTERNAL 5000 not combinable; Communotron 16
    /// DIRECT 5e5 combinable; RC-L01 (probeStackLarge) ModuleProbeControlPoint minimumCrew=1
    /// multiHop=True; Mk1-3 pod minimumCrew=2 multiHop=False.
    /// </summary>
    [Collection("Sequential")]
    public class GhostCommNetTests : IDisposable
    {
        private const double RA2 = 2e9;
        private readonly List<string> logLines = new List<string>();

        public GhostCommNetTests()
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

        private static GhostAntennaPowerInput Relay(double p, bool comb = true, double e = 0.75)
            => new GhostAntennaPowerInput { Power = p, IsRelay = true, Combinable = comb, Exponent = e };

        private static GhostAntennaPowerInput Direct(double p, bool comb = true, double e = 0.75)
            => new GhostAntennaPowerInput { Power = p, IsRelay = false, Combinable = comb, Exponent = e };

        private static void Near(double expected, double actual, double relTol = 1e-12)
        {
            double tol = Math.Max(1e-9, Math.Abs(expected) * relTol);
            Assert.True(Math.Abs(expected - actual) <= tol,
                string.Format(CultureInfo.InvariantCulture, "expected {0:R} got {1:R}", expected, actual));
        }

        // ---------------------------------------------------------------- power combination

        [Fact]
        public void TwoRelays_WithProbeCoreInternal_ExponentWeighted()
        {
            // Order: internal(5000, not comb), RA-2, RA-2.
            // N: maxDirect=5000, SN=0. R: SR=4e9, ER=0.75*4e9=3e9, maxCR=2e9, maxR=2e9.
            // total=SN+SR=4e9, strongest=maxCR=2e9 (0 > 2e9 false), y=(0+3e9)/4e9=0.75,
            // total=2e9*2^0.75=3.3635856610148582e9 (unused: SN=0 so transmit=maxDirect=5000).
            // SR != maxCR -> SR=2e9*(4e9/2e9)^(3e9/4e9)=3.3635856610148582e9.
            // relay: SR > maxR -> 3.3635856610148582e9, combined.
            var r = GhostCommNetMath.ComputeVesselPowers(
                new[] { Direct(5000, comb: false), Relay(RA2), Relay(RA2) }, false, 1.0);
            Near(3363585661.0148582, r.RelayPower);
            Assert.True(r.RelayCombined);
            Near(5000.0, r.TransmitPower);
            Assert.False(r.TransmitCombined);
            Assert.Equal(0, r.TransmitAntenna);
            Assert.Equal(1, r.RelayAntenna);   // strongest combinable relay: the first RA-2
            Assert.Equal(1, r.ScienceAntenna); // relay > transmit -> relay's science curve
        }

        [Fact]
        public void TwoRelays_NoOtherAntenna_StockRawSumQuirk()
        {
            // No non-relay antenna: stock skips the exponent block, SR stays the raw sum 4e9.
            // relay = max(SR=4e9, maxR=2e9) = 4e9, transmit = 0.
            var r = GhostCommNetMath.ComputeVesselPowers(new[] { Relay(RA2), Relay(RA2) }, false, 1.0);
            Near(4e9, r.RelayPower);
            Assert.True(r.RelayCombined);
            Assert.Equal(0.0, r.TransmitPower);
            Assert.Equal(-1, r.TransmitAntenna);
            Assert.Equal(0, r.RelayAntenna);
            Assert.Equal(0, r.ScienceAntenna);
        }

        [Fact]
        public void SingleRelay_NotCombinedAgainstItself()
        {
            // SR=2e9 equals maxR=2e9 -> relay = maxR, combined=false.
            var r = GhostCommNetMath.ComputeVesselPowers(new[] { Relay(RA2) }, false, 1.0);
            Near(RA2, r.RelayPower);
            Assert.False(r.RelayCombined);
            Assert.Equal(0.0, r.TransmitPower);
        }

        [Fact]
        public void NonCombinableStrongest_WinsTransmit_NoDraftSum()
        {
            // A DIRECT 5e6 not comb, B DIRECT 1e6 comb e=.75, C DIRECT 5e5 comb e=.75.
            // maxDirect=5e6 (A); SN=1.5e6, EN=1.125e6, maxCN=1e6 (B).
            // total=1.5e6, strongest=1e6, y=1.125e6/1.5e6=0.75, total=1e6*1.5^0.75=1.3554030054e6.
            // total (1.355e6) > maxDirect (5e6)? no -> transmit = 5e6 from A, not combined.
            // (The removed draft computed 5e6 + 1e6*0.2^0.75 + 5e5*0.1^0.75, larger than stock.)
            var r = GhostCommNetMath.ComputeVesselPowers(
                new[] { Direct(5e6, comb: false, e: 1.0), Direct(1e6), Direct(5e5) }, false, 1.0);
            Near(5e6, r.TransmitPower);
            Assert.False(r.TransmitCombined);
            Assert.Equal(0, r.TransmitAntenna);
            Assert.Equal(0.0, r.RelayPower);
            Assert.Equal(-1, r.RelayAntenna);
        }

        [Fact]
        public void TwoCombinableDirect_Combined()
        {
            // SN=1e6, EN=7.5e5, maxCN=5e5 (first wins ties), maxDirect=5e5.
            // total=5e5*(1e6/5e5)^0.75=840896.4152537145 > 5e5 -> combined; no combinable relay
            // -> antenna = bestCombDirect (index 0).
            var r = GhostCommNetMath.ComputeVesselPowers(new[] { Direct(5e5), Direct(5e5) }, false, 1.0);
            Near(840896.4152537145, r.TransmitPower);
            Assert.True(r.TransmitCombined);
            Assert.Equal(0, r.TransmitAntenna);
        }

        [Fact]
        public void MixedExponents_TransmitCombinesWithRelays()
        {
            // A DIRECT 1e6 comb e=0.5; B RELAY 2e6 comb e=1.0.
            // SN=1e6 EN=5e5 maxCN=1e6; SR=2e6 ER=2e6 maxCR=2e6; maxDirect=1e6, maxR=2e6.
            // total=3e6; directStronger = 1e6 > 2e6 = false -> strongest=2e6;
            // y=(5e5+2e6)/3e6=0.8333..; total=2e6*1.5^0.8333=2803965.795552201.
            // SR == maxCR -> unchanged 2e6. total > maxDirect -> transmit combined, antenna =
            // bestCombRelay (B, index 1) since the relay side is stronger.
            // relay: SR 2e6 > maxR 2e6? no -> relay=2e6 (B), not combined.
            // science: relay (2e6) > transmit (2.80e6)? no -> transmit antenna (B).
            var r = GhostCommNetMath.ComputeVesselPowers(
                new[] { Direct(1e6, e: 0.5), Relay(2e6, e: 1.0) }, false, 1.0);
            Near(2803965.795552201, r.TransmitPower);
            Assert.True(r.TransmitCombined);
            Assert.Equal(1, r.TransmitAntenna);
            Near(2e6, r.RelayPower);
            Assert.False(r.RelayCombined);
            Assert.Equal(1, r.RelayAntenna);
            Assert.Equal(1, r.ScienceAntenna);
        }

        [Fact]
        public void RangeModifier_ScalesBothPowersAfterCombination()
        {
            // Same as TwoRelays_WithProbeCoreInternal with rangeModifier 0.5:
            // relay 3.3635856610148582e9*0.5 = 1.6817928305074291e9, transmit 5000*0.5 = 2500.
            var r = GhostCommNetMath.ComputeVesselPowers(
                new[] { Direct(5000, comb: false), Relay(RA2), Relay(RA2) }, false, (double)0.5f);
            Near(1681792830.5074291, r.RelayPower);
            Near(2500.0, r.TransmitPower);
        }

        [Fact]
        public void RelayEnabler_PromotesTransmitToRelay()
        {
            // DIRECT 5e5 only: transmit=5e5, relay=0; an active IRelayEnabler moves it to relay.
            var r = GhostCommNetMath.ComputeVesselPowers(new[] { Direct(5e5, comb: false) }, true, 1.0);
            Near(5e5, r.RelayPower);
            Assert.Equal(0.0, r.TransmitPower);
            Assert.Equal(0, r.RelayAntenna);
        }

        [Fact]
        public void NoAntennas_AllZero()
        {
            var r = GhostCommNetMath.ComputeVesselPowers(new List<GhostAntennaPowerInput>(), false, 1.0);
            Assert.Equal(0.0, r.RelayPower);
            Assert.Equal(0.0, r.TransmitPower);
            Assert.Equal(-1, r.RelayAntenna);
            Assert.Equal(-1, r.TransmitAntenna);
            Assert.Equal(-1, r.ScienceAntenna);
        }

        [Fact]
        public void ZeroPowerAntenna_NeverBecomesBest()
        {
            // Stock picks a best antenna only on p > max (max starts at 0).
            var r = GhostCommNetMath.ComputeVesselPowers(new[] { Direct(0, comb: false), Relay(0, comb: false) }, false, 1.0);
            Assert.Equal(-1, r.TransmitAntenna);
            Assert.Equal(-1, r.RelayAntenna);
        }

        // ---------------------------------------------------------------- control point

        private static List<GhostControlPointSpec> Cp(int minCrew, bool multiHop, bool canOperate = true)
            => new List<GhostControlPointSpec>
            {
                new GhostControlPointSpec { MinimumCrew = minCrew, MultiHop = multiHop, CanOperate = canOperate }
            };

        [Theory]
        [InlineData(0, false, true)]  // RC-L01 without a pilot: multiHop still set (stock), no control
        [InlineData(1, true, true)]
        [InlineData(2, true, true)]
        public void RcL01_MinimumCrewOne(int pilots, bool expectControl, bool expectMultiHop)
        {
            GhostCommNetMath.DecideControlPoint(Cp(1, true), pilots, out bool control, out bool multiHop);
            Assert.Equal(expectControl, control);
            Assert.Equal(expectMultiHop, multiHop);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, true)]
        public void Mk13Pod_MinimumCrewTwo_SingleHop(int pilots, bool expectControl)
        {
            GhostCommNetMath.DecideControlPoint(Cp(2, false), pilots, out bool control, out bool multiHop);
            Assert.Equal(expectControl, control);
            Assert.False(multiHop);
        }

        [Fact]
        public void MinimumCrewZero_ControlWithoutCrew()
        {
            GhostCommNetMath.DecideControlPoint(Cp(0, false), 0, out bool control, out _);
            Assert.True(control);
        }

        [Fact]
        public void BrokenControlPoint_Ignored()
        {
            GhostCommNetMath.DecideControlPoint(Cp(0, true, canOperate: false), 5, out bool control, out bool multiHop);
            Assert.False(control);
            Assert.False(multiHop);
        }

        // ---------------------------------------------------------------- timeline

        private const uint CorePid = 100000;
        private const uint DecouplerPid = 101111;
        private const uint RelayPid = 102222;
        private const uint DishPid = 103333;

        /// <summary>
        /// probeStackLarge (root, internal 5000 + control point) -> decoupler -> RA-2 relay,
        /// plus a deploy-gated HG-5 (RELAY 5e6) on the root. Stock iteration order is parts
        /// last-to-first, so the antenna list is HG-5, RA-2, internal.
        /// </summary>
        private static GhostCommNetVesselSpec RelaySatSpec(bool dishSnapshotCanComm = false, string pilot = "Val")
        {
            var spec = new GhostCommNetVesselSpec { VesselName = "Relay Sat", VesselTypeName = "Relay" };
            spec.Parts.Add(new GhostPartNodeSpec { Pid = CorePid, ParentIndex = 0 });
            spec.Parts.Add(new GhostPartNodeSpec { Pid = DecouplerPid, ParentIndex = 0 });
            spec.Parts.Add(new GhostPartNodeSpec { Pid = RelayPid, ParentIndex = 1 });
            spec.Parts.Add(new GhostPartNodeSpec { Pid = DishPid, ParentIndex = 0 });
            spec.Antennas.Add(new GhostAntennaSpec
            {
                PartPid = DishPid, Power = 5e6, IsRelay = true, Combinable = true, Exponent = 0.75,
                SnapshotCanComm = dishSnapshotCanComm, DeployGated = true,
            });
            spec.Antennas.Add(new GhostAntennaSpec
            {
                PartPid = RelayPid, Power = RA2, IsRelay = true, Combinable = true, Exponent = 0.75,
                SnapshotCanComm = true,
            });
            spec.Antennas.Add(new GhostAntennaSpec
            {
                PartPid = CorePid, Power = 5000, IsRelay = false, Combinable = false, Exponent = 0.75,
                SnapshotCanComm = true,
            });
            spec.ControlPoints.Add(new GhostControlPointSpec
            {
                PartPid = CorePid, MinimumCrew = 1, MultiHop = true, CanOperate = true,
            });
            if (pilot != null)
                spec.Crew.Add(new GhostCrewSpec { PartPid = CorePid, Name = pilot, Known = true, Qualifies = true });
            return spec;
        }

        private static PartEvent Ev(double ut, uint pid, PartEventType type)
            => new PartEvent { ut = ut, partPersistentId = pid, eventType = type };

        [Fact]
        public void Timeline_NoEvents_SingleState()
        {
            // HG-5 snapshot canComm=false, no events -> inactive. Active: RA-2 + internal.
            // relay = RA-2 alone = 2e9 (SR == maxR, not combined); control (1 pilot >= 1).
            var t = GhostCommNetMath.BuildTimeline(RelaySatSpec(), new List<PartEvent>());
            Assert.Equal(1, t.Count);
            GhostCommNetState s = t.Sample(1000);
            Near(RA2, s.Powers.RelayPower);
            Near(5000, s.Powers.TransmitPower);
            Assert.True(s.IsControlSource);
            Assert.True(s.IsControlSourceMultiHop);
            Assert.Equal(2, s.ActiveAntennas);
            Assert.True(t.HasCapability);
        }

        [Fact]
        public void Timeline_DeployExtendRetractBreak()
        {
            var events = new List<PartEvent>
            {
                Ev(100, DishPid, PartEventType.DeployableExtended),
                Ev(200, DishPid, PartEventType.DeployableRetracted),
                Ev(250, DishPid, PartEventType.DeployableExtended),
                Ev(300, DishPid, PartEventType.DeployableBroken),
            };
            var t = GhostCommNetMath.BuildTimeline(RelaySatSpec(dishSnapshotCanComm: true), events);
            // Before the first event (Extended) the dish was retracted: relay = 2e9.
            Near(RA2, t.Sample(50).Powers.RelayPower);
            // Extended [100,200) and [250,300): HG-5 5e6 + RA-2 2e9, internal present:
            // SR=2.005e9, ER=0.75*SR, maxCR=2e9 -> SR = 2e9*(2.005e9/2e9)^0.75.
            double both = 2e9 * Math.Pow(2.005e9 / 2e9, 0.75);
            Near(both, t.Sample(100).Powers.RelayPower);
            Near(both, t.Sample(199.9).Powers.RelayPower);
            Near(RA2, t.Sample(200).Powers.RelayPower);
            Near(both, t.Sample(260).Powers.RelayPower);
            Near(RA2, t.Sample(300).Powers.RelayPower);
            Near(RA2, t.Sample(1e9).Powers.RelayPower);
        }

        [Fact]
        public void DeployGated_BeforeFirstEvent_IsOppositeOfFirstEvent()
        {
            var ext = new List<PartEvent> { Ev(10, DishPid, PartEventType.DeployableExtended) };
            var ret = new List<PartEvent> { Ev(10, DishPid, PartEventType.DeployableRetracted) };
            var brk = new List<PartEvent> { Ev(10, DishPid, PartEventType.DeployableBroken) };
            Assert.False(GhostCommNetMath.DeployGatedCanCommAt(DishPid, ext, 5, true));
            Assert.True(GhostCommNetMath.DeployGatedCanCommAt(DishPid, ret, 5, false));
            Assert.True(GhostCommNetMath.DeployGatedCanCommAt(DishPid, brk, 5, false));
            Assert.True(GhostCommNetMath.DeployGatedCanCommAt(DishPid, ext, 10, false));
            Assert.False(GhostCommNetMath.DeployGatedCanCommAt(DishPid, brk, 10, true));
            // No events for this part: the snapshot decides.
            Assert.True(GhostCommNetMath.DeployGatedCanCommAt(DishPid, new List<PartEvent>(), 5, true));
            Assert.False(GhostCommNetMath.DeployGatedCanCommAt(DishPid, new List<PartEvent>(), 5, false));
        }

        [Fact]
        public void Timeline_DecoupleRemovesSubtree_DestroyRemovesPart()
        {
            // Decoupling the decoupler at 500 removes the RA-2 below it (subtree), as ghost
            // playback hides it. Afterwards only the internal antenna remains: relay 0.
            var events = new List<PartEvent> { Ev(500, DecouplerPid, PartEventType.Decoupled) };
            var t = GhostCommNetMath.BuildTimeline(RelaySatSpec(), events);
            Near(RA2, t.Sample(499).Powers.RelayPower);
            Assert.Equal(0.0, t.Sample(500).Powers.RelayPower);
            Near(5000, t.Sample(500).Powers.TransmitPower);

            // Destroying the core at 700 removes its antenna, control point and pilot.
            var events2 = new List<PartEvent> { Ev(700, CorePid, PartEventType.Destroyed) };
            var t2 = GhostCommNetMath.BuildTimeline(RelaySatSpec(), events2);
            Assert.True(t2.Sample(600).IsControlSource);
            GhostCommNetState after = t2.Sample(700);
            Assert.False(after.IsControlSource);
            Assert.Equal(0.0, after.Powers.TransmitPower);
            // Only relays left, N empty: raw-sum quirk, one relay -> 2e9.
            Near(RA2, after.Powers.RelayPower);
        }

        [Fact]
        public void Timeline_ControlPoint_NoPilot_AndUnknownCrew()
        {
            var noPilot = GhostCommNetMath.BuildTimeline(RelaySatSpec(pilot: null), null);
            Assert.False(noPilot.Sample(0).IsControlSource);
            Assert.True(noPilot.Sample(0).IsControlSourceMultiHop); // stock sets multiHop independently

            var spec = RelaySatSpec(pilot: null);
            spec.Crew.Add(new GhostCrewSpec { PartPid = CorePid, Name = "Ghost Kerman", Known = false, Qualifies = true });
            var unknown = GhostCommNetMath.BuildTimeline(spec, null);
            Assert.False(unknown.Sample(0).IsControlSource);
        }

        [Fact]
        public void Timeline_Capability()
        {
            var directOnly = new GhostCommNetVesselSpec();
            directOnly.Parts.Add(new GhostPartNodeSpec { Pid = 1, ParentIndex = 0 });
            directOnly.Antennas.Add(new GhostAntennaSpec { PartPid = 1, Power = 5e5, SnapshotCanComm = true });
            Assert.False(GhostCommNetMath.BuildTimeline(directOnly, null).HasCapability);

            directOnly.ControlPoints.Add(new GhostControlPointSpec { PartPid = 1, MinimumCrew = 0, CanOperate = true });
            Assert.True(GhostCommNetMath.BuildTimeline(directOnly, null).HasCapability);

            var controlNoAntenna = new GhostCommNetVesselSpec();
            controlNoAntenna.ControlPoints.Add(new GhostControlPointSpec { PartPid = 1, MinimumCrew = 0, CanOperate = true });
            Assert.False(GhostCommNetMath.BuildTimeline(controlNoAntenna, null).HasCapability);
        }

        [Fact]
        public void Timeline_SameUtEventsCollapse_AndUnrelatedEventsIgnored()
        {
            var events = new List<PartEvent>
            {
                Ev(100, DishPid, PartEventType.DeployableExtended),
                Ev(100, 999999, PartEventType.DeployableExtended),   // not a snapshot part
                Ev(150, RelayPid, PartEventType.EngineIgnited),      // not a CommNet event
                Ev(100, DishPid, PartEventType.DeployableRetracted), // same UT, later in list wins
            };
            var t = GhostCommNetMath.BuildTimeline(RelaySatSpec(), events);
            Assert.Equal(1, t.Count);
            Near(RA2, t.Sample(120).Powers.RelayPower);
        }

        // ---------------------------------------------------------------- eligibility

        private static GhostCommNetEligibilityInput InWindowInput()
            => new GhostCommNetEligibilityInput
            {
                HasRecordingId = true,
                HasRenderableData = true,
                ActivationStartUT = 100,
                EndUT = 200,
                ChainEndUT = 200,
            };

        [Fact]
        public void Eligibility_InWindow_Edges()
        {
            var i = InWindowInput();
            Assert.Equal("before-window", GhostCommNetMath.EvaluateEligibility(i, 99.9).Reason);
            var at = GhostCommNetMath.EvaluateEligibility(i, 100);
            Assert.True(at.Eligible);
            Assert.False(at.HoldAtEnd);
            Assert.Equal("in-window", at.Reason);
            Assert.True(GhostCommNetMath.EvaluateEligibility(i, 200).Eligible);
            Assert.Equal("window-ended", GhostCommNetMath.EvaluateEligibility(i, 200.1).Reason);
        }

        [Fact]
        public void Eligibility_HiddenGhost_IsNotAnInput_StillRelays()
        {
            // The playback-enabled toggle has no field in the input: a hidden recording in
            // its window is eligible (scenario 12).
            Assert.True(GhostCommNetMath.EvaluateEligibility(InWindowInput(), 150).Eligible);
        }

        [Theory]
        [InlineData("no-recording-id")]
        [InlineData("debris")]
        [InlineData("no-trajectory")]
        [InlineData("rewind-retired")]
        [InlineData("superseded")]
        [InlineData("re-fly-session-suppressed")]
        [InlineData("real-vessel-exists")]
        [InlineData("historical-never-replayed")]
        public void Eligibility_Exclusions(string reason)
        {
            var i = InWindowInput();
            switch (reason)
            {
                case "no-recording-id": i.HasRecordingId = false; break;
                case "debris": i.IsDebris = true; break;
                case "no-trajectory": i.HasRenderableData = false; break;
                case "rewind-retired": i.RewindRetired = true; break;
                case "superseded": i.SupersededByRelation = true; break;
                case "re-fly-session-suppressed": i.SessionSuppressed = true; break;
                case "real-vessel-exists": i.ExternalVesselSuppressed = true; break;
                case "historical-never-replayed": i.HistoricalNeverReplayed = true; break;
            }
            var e = GhostCommNetMath.EvaluateEligibility(i, 150);
            Assert.False(e.Eligible);
            Assert.Equal(reason, e.Reason);
        }

        [Fact]
        public void Eligibility_HeldForSpawn_UntilSpawnedOrAbandoned()
        {
            var i = InWindowInput();
            i.NeedsSpawn = true;
            var held = GhostCommNetMath.EvaluateEligibility(i, 500);
            Assert.True(held.Eligible);
            Assert.True(held.HoldAtEnd);
            Assert.Equal("held-for-spawn", held.Reason);

            i.VesselSpawned = true;
            Assert.Equal("vessel-spawned", GhostCommNetMath.EvaluateEligibility(i, 500).Reason);

            i.VesselSpawned = false;
            i.SpawnAbandoned = true;
            Assert.Equal("spawn-abandoned", GhostCommNetMath.EvaluateEligibility(i, 500).Reason);

            i.SpawnAbandoned = false;
            i.CannotSpawnSafely = true;
            Assert.Equal("spawn-abandoned", GhostCommNetMath.EvaluateEligibility(i, 500).Reason);
        }

        [Fact]
        public void Eligibility_MidChainGap_HoldsUntilSuccessorStarts()
        {
            var i = InWindowInput();
            i.IsMidChain = true;
            i.ChainEndUT = 400;
            var gap = GhostCommNetMath.EvaluateEligibility(i, 250);
            Assert.True(gap.Eligible);
            Assert.True(gap.HoldAtEnd);
            Assert.Equal("chain-gap-hold", gap.Reason);
            i.ChainSuccessorStarted = true;
            Assert.False(GhostCommNetMath.EvaluateEligibility(i, 250).Eligible);
            i.ChainSuccessorStarted = false;
            Assert.False(GhostCommNetMath.EvaluateEligibility(i, 401).Eligible);
        }

        [Fact]
        public void Eligibility_LoopRecording_OnlyItsOwnWindow()
        {
            // A looping recording offers its own UT window only; a later loop cycle (UT well
            // past EndUT with no spawn pending) carries no signal (scenario 11).
            var i = InWindowInput();
            Assert.True(GhostCommNetMath.EvaluateEligibility(i, 150).Eligible);
            Assert.False(GhostCommNetMath.EvaluateEligibility(i, 150 + 3600).Eligible);
        }

        // ---------------------------------------------------------------- hook sample / held position

        [Fact]
        public void HookSample_HeldThroughWarp_StateAtEnd_PositionAtNow()
        {
            // A relay recording ending in Duna orbit at UT 200, spawn deferred through a
            // one-day warp: at UT 200+21600 the node is held. The antenna state is the END
            // state (StateUT = EndUT), and Held tells the hook to place it from the held
            // source at the CURRENT UT, so it stays with Duna instead of trailing where Duna
            // was at EndUT.
            var s = GhostCommNetMath.ResolveHookSample(200 + 21600, 100, 200, holdAtEnd: true, expectHoldPastEnd: true);
            Assert.True(s.Lit);
            Assert.True(s.Held);
            Assert.Equal(200.0, s.StateUT);
        }

        [Fact]
        public void HookSample_AllBranches()
        {
            var before = GhostCommNetMath.ResolveHookSample(50, 100, 200, false, false);
            Assert.False(before.Lit);
            Assert.Equal("before-window", before.DarkReason);

            var inWindow = GhostCommNetMath.ResolveHookSample(150, 100, 200, false, true);
            Assert.True(inWindow.Lit);
            Assert.False(inWindow.Held);
            Assert.Equal(150.0, inWindow.StateUT);

            // Past EndUT between host ticks, a hold already expected: bridged, no seam gap.
            var bridged = GhostCommNetMath.ResolveHookSample(201, 100, 200, false, true);
            Assert.True(bridged.Lit);
            Assert.True(bridged.Held);
            Assert.Equal(200.0, bridged.StateUT);

            // Past EndUT, no hold (destroyed vessel, scenario 10): dark on this very rebuild,
            // even at high warp before the Tracking Station's next 0.25 s tick removes it.
            var destroyed = GhostCommNetMath.ResolveHookSample(200 + 5000, 100, 200, false, false);
            Assert.False(destroyed.Lit);
            Assert.Equal("past-end", destroyed.DarkReason);

            // A host hold wins even before the window start (never happens live; defensive).
            Assert.True(GhostCommNetMath.ResolveHookSample(10, 100, 200, true, false).Lit);
        }

        [Fact]
        public void ExpectsHoldPastEnd_FromSpawnOrChainGap()
        {
            var i = InWindowInput();
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(i));
            i.NeedsSpawn = true;
            Assert.True(GhostCommNetMath.ExpectsHoldPastEnd(i));
            Assert.True(GhostCommNetMath.EvaluateEligibility(i, 150).ExpectHoldPastEnd);
            i.SpawnAbandoned = true;
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(i));
            i.SpawnAbandoned = false;
            i.VesselSpawned = true;
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(i));

            var chain = InWindowInput();
            chain.IsMidChain = true;
            chain.ChainEndUT = 400;
            Assert.True(GhostCommNetMath.ExpectsHoldPastEnd(chain));
            chain.ChainEndUT = 200;
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(chain));
        }

        [Theory]
        // surfacePos, terminalIsSurface, orbit, endBodyFixed -> source (0 None, 1 TerminalSurface, 2 TerminalOrbit, 3 RecordedEndBodyFixed)
        [InlineData(true, true, false, true, 1)]
        [InlineData(true, false, true, false, 1)]
        [InlineData(false, false, true, false, 2)]
        [InlineData(false, false, true, true, 2)]
        [InlineData(false, true, true, true, 3)]
        [InlineData(false, false, false, true, 3)]
        [InlineData(false, false, false, false, 0)]
        [InlineData(false, true, false, false, 0)]
        public void ChooseHeldPositionSource_Precedence(
            bool surfacePos, bool terminalIsSurface, bool orbit, bool endBodyFixed,
            int expected)
        {
            Assert.Equal((GhostCommNetHeldPositionSource)expected,
                GhostCommNetMath.ChooseHeldPositionSource(surfacePos, terminalIsSurface, orbit, endBodyFixed));
        }

        [Fact]
        public void LogHeldSource_OneLine()
        {
            GhostCommNetMath.LogHeldSource("rec7", "Duna Relay", "FLIGHT",
                GhostCommNetHeldPositionSource.TerminalOrbit, 200, "terminal=Orbiting orbit=True body=Duna");
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]")
                && l.Contains("Held ghost node position source: key=rec7 vessel=\"Duna Relay\" scene=FLIGHT source=TerminalOrbit endUT=200.0")
                && l.Contains("body=Duna"));
        }

        [Fact]
        public void FormatHeldPosition_ReportsRadiusAndSweptAngleInvariantly()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal culture proves the line is invariant (the harness regexes it).
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostCommNetMath.FormatHeldPosition("rec7", "Held Relay", "FLIGHT",
                    GhostCommNetHeldPositionSource.TerminalOrbit, 471.26, 371.16, 3463334.49, 1.6708);
                Assert.Equal("Held ghost node position: key=rec7 vessel=\"Held Relay\" scene=FLIGHT "
                    + "source=TerminalOrbit ut=471.3 sinceEnd=100.1 radius=3463334 sweptDeg=1.67", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------------------------------------------------------------- stock plumbing

        [Theory]
        [InlineData("Debris", false)]
        [InlineData("SpaceObject", false)]
        [InlineData("Unknown", false)]
        [InlineData("Flag", false)]
        [InlineData("DeployedSciencePart", false)]
        [InlineData("Probe", true)]
        [InlineData("Relay", true)]
        [InlineData("Ship", true)]
        [InlineData("EVA", true)]
        [InlineData("DeployedScienceController", true)]
        [InlineData("DroppedPart", true)]
        [InlineData(null, true)]
        [InlineData("NotAType", true)]
        public void VesselTypeGetsNode_MirrorsStock(string type, bool expected)
        {
            Assert.Equal(expected, GhostCommNetMath.VesselTypeGetsNode(type));
        }

        [Fact]
        public void ResolveSnapshotModuleIndex_StockFindModuleSemantics()
        {
            var names = new List<string> { "ModuleCommand", "ModuleDataTransmitter", "ModuleSAS", "ModuleDataTransmitter" };
            Assert.Equal(1, GhostCommNetMath.ResolveSnapshotModuleIndex(names, 1, "ModuleDataTransmitter"));
            // Index points at another module: the LAST module with the name.
            Assert.Equal(3, GhostCommNetMath.ResolveSnapshotModuleIndex(names, 2, "ModuleDataTransmitter"));
            // Index past the end: by name.
            Assert.Equal(3, GhostCommNetMath.ResolveSnapshotModuleIndex(names, 9, "ModuleDataTransmitter"));
            Assert.Equal(-1, GhostCommNetMath.ResolveSnapshotModuleIndex(names, 0, "ModuleProbeControlPoint"));
            Assert.Equal(-1, GhostCommNetMath.ResolveSnapshotModuleIndex(null, 0, "X"));
        }

        [Fact]
        public void DecideAvailability_AllCases()
        {
            Assert.Equal(GhostCommNetAvailability.CommNetDisabled,
                GhostCommNetMath.DecideAvailability(false, null, null, null));
            Assert.Equal(GhostCommNetAvailability.NetworkNotReady,
                GhostCommNetMath.DecideAvailability(true, null, null, typeof(CommNet.CommRangeModel)));
            Assert.Equal(GhostCommNetAvailability.NetworkNotReady,
                GhostCommNetMath.DecideAvailability(true, typeof(CommNet.CommNetNetwork), null, typeof(CommNet.CommRangeModel)));
            Assert.Equal(GhostCommNetAvailability.Available,
                GhostCommNetMath.DecideAvailability(true, typeof(CommNet.CommNetNetwork),
                    typeof(CommNet.CommNetwork), typeof(CommNet.CommRangeModel)));
            // A subclass of the network (CommNetManager-style) or a different range model is foreign.
            Assert.Equal(GhostCommNetAvailability.ForeignNetworkTypes,
                GhostCommNetMath.DecideAvailability(true, typeof(FakeNetwork),
                    typeof(CommNet.CommNetwork), typeof(CommNet.CommRangeModel)));
            Assert.Equal(GhostCommNetAvailability.ForeignNetworkTypes,
                GhostCommNetMath.DecideAvailability(true, typeof(CommNet.CommNetNetwork),
                    typeof(FakeCommNetwork), typeof(CommNet.CommRangeModel)));
            Assert.Equal(GhostCommNetAvailability.ForeignNetworkTypes,
                GhostCommNetMath.DecideAvailability(true, typeof(CommNet.CommNetNetwork),
                    typeof(CommNet.CommNetwork), typeof(FakeRangeModel)));
        }

        private class FakeNetwork : CommNet.CommNetNetwork { }
        private class FakeCommNetwork : CommNet.CommNetwork { }
        private class FakeRangeModel : CommNet.CommRangeModel { }

        [Fact]
        public void RegistrationDiff_AddRemoveKeep()
        {
            var toAdd = new List<string>();
            var toRemove = new List<string>();
            GhostCommNetMath.ComputeRegistrationDiff(
                new HashSet<string> { "b", "c", "a" }, new List<string> { "c", "d" }, toAdd, toRemove);
            Assert.Equal(new[] { "a", "b" }, toAdd.ToArray());
            Assert.Equal(new[] { "d" }, toRemove.ToArray());

            GhostCommNetMath.ComputeRegistrationDiff(null, new List<string> { "x" }, toAdd, toRemove);
            Assert.Empty(toAdd);
            Assert.Equal(new[] { "x" }, toRemove.ToArray());
        }

        [Fact]
        public void ChainVesselCoveredByRecording_LaunchIdentity()
        {
            string g1 = "aaaaaaaa-0000-0000-0000-000000000001";
            string g2 = "aaaaaaaa-0000-0000-0000-000000000002";
            var ids = new List<KeyValuePair<uint, string>> { new KeyValuePair<uint, string>(42, g1) };
            Assert.True(GhostCommNetMath.ChainVesselCoveredByRecording(42, g1, ids));
            Assert.True(GhostCommNetMath.ChainVesselCoveredByRecording(42, null, ids));
            Assert.False(GhostCommNetMath.ChainVesselCoveredByRecording(42, g2, ids));
            Assert.False(GhostCommNetMath.ChainVesselCoveredByRecording(43, g1, ids));
            Assert.False(GhostCommNetMath.ChainVesselCoveredByRecording(42, g1, null));
        }

        [Fact]
        public void NodeNames_KeyedByRecordingId()
        {
            Assert.Equal("ParsekGhost:abc", GhostCommNetMath.NodeNameForRecording("abc"));
            Assert.Equal("chain:7", GhostCommNetMath.ChainKey(7));
        }

        [Fact]
        public void RelaySatelliteBuilder_EmitsStockRelayAndControlParts()
        {
            ConfigNode v = Generators.VesselSnapshotBuilder.RelaySatellite("Relay One", 4242, pilot: "Valentina Kerman").Build();
            Assert.Equal("Relay", v.GetValue("type"));
            Assert.True(GhostCommNetMath.VesselTypeGetsNode(v.GetValue("type")));
            ConfigNode[] parts = v.GetNodes("PART");
            Assert.Equal(3, parts.Length);
            Assert.Equal("probeStackLarge", parts[0].GetValue("name"));
            Assert.Equal("RelayAntenna5", parts[1].GetValue("name"));
            Assert.Equal("mk1pod.v2", parts[2].GetValue("name"));
            Assert.Equal("Valentina Kerman", parts[2].GetValue("crew"));
            Assert.Equal("0", parts[1].GetValue("parent"));
            var rootModules = new List<string>();
            foreach (ConfigNode m in parts[0].GetNodes("MODULE")) rootModules.Add(m.GetValue("name"));
            Assert.Equal(1, GhostCommNetMath.ResolveSnapshotModuleIndex(rootModules, 1, "ModuleCommand"));
            Assert.Equal(2, GhostCommNetMath.ResolveSnapshotModuleIndex(rootModules, 5, "ModuleDataTransmitter"));
            Assert.Equal(0, GhostCommNetMath.ResolveSnapshotModuleIndex(rootModules, 0, "ModuleProbeControlPoint"));
            Assert.Equal("True", parts[1].GetNode("MODULE").GetValue("canComm"));
            // Antenna power is prefab-only, never in the snapshot.
            Assert.Null(parts[1].GetNode("MODULE").GetValue("antennaPower"));
        }

        // ---------------------------------------------------------------- continuation hold (scenario 15)

        private const string GuidA = "aaaaaaaa-0000-0000-0000-00000000000a";
        private const string GuidB = "bbbbbbbb-0000-0000-0000-00000000000b";

        private static Recording Rec(string id, uint pid, string guid, double start, double end,
            TerminalState? terminal = TerminalState.Orbiting)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Station " + id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                TerminalStateValue = terminal,
            };
        }

        [Fact]
        public void Eligibility_ContinuationHold_HoldsUntilTakeover()
        {
            var i = InWindowInput();
            i.HasContinuationHold = true;
            i.ContinuationHoldUntilUT = 500;
            var inWindow = GhostCommNetMath.EvaluateEligibility(i, 150);
            Assert.True(inWindow.Eligible);
            Assert.True(inWindow.ExpectHoldPastEnd);
            var held = GhostCommNetMath.EvaluateEligibility(i, 300);
            Assert.True(held.Eligible);
            Assert.True(held.HoldAtEnd);
            Assert.Equal("continuation-gap-hold", held.Reason);
            // The takeover UT releases the node (the continuation's own node carries on).
            Assert.Equal("window-ended", GhostCommNetMath.EvaluateEligibility(i, 500).Reason);
            // No hold flag, no hold, whatever the UT field says.
            i.HasContinuationHold = false;
            Assert.Equal("window-ended", GhostCommNetMath.EvaluateEligibility(i, 300).Reason);
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(i));
        }

        [Fact]
        public void Eligibility_ContinuationHold_NeverOverridesExclusionsOrSpawn()
        {
            var i = InWindowInput();
            i.HasContinuationHold = true;
            i.ContinuationHoldUntilUT = 500;
            i.VesselSpawned = true;
            Assert.Equal("vessel-spawned", GhostCommNetMath.EvaluateEligibility(i, 300).Reason);
            i.VesselSpawned = false;
            i.HistoricalNeverReplayed = true;
            Assert.Equal("historical-never-replayed", GhostCommNetMath.EvaluateEligibility(i, 300).Reason);
            i.HistoricalNeverReplayed = false;
            i.NeedsSpawn = true;
            Assert.Equal("held-for-spawn", GhostCommNetMath.EvaluateEligibility(i, 300).Reason);
            // A hold that ends at or before EndUT is no hold.
            var j = InWindowInput();
            j.HasContinuationHold = true;
            j.ContinuationHoldUntilUT = 200;
            Assert.False(GhostCommNetMath.ExpectsHoldPastEnd(j));
            Assert.Equal("window-ended", GhostCommNetMath.EvaluateEligibility(j, 200.5).Reason);
        }

        [Fact]
        public void ResolveContinuationHoldUntilUT_EarliestTakeover()
        {
            var noCarriers = new List<KeyValuePair<double, double>>();
            // Claims before the end are history; the first claim at or after it wins.
            Assert.Equal(900.0, GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200, new List<double> { 50, 900, 1200 }, noCarriers));
            // A claim exactly at the end (within tolerance) ends the hold at once.
            Assert.Equal(200.0, GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200.0005, new List<double> { 200 }, noCarriers));
            // A later carrier recording of the vessel starts before the next claim.
            Assert.Equal(700.0, GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200, new List<double> { 900 },
                new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(700, 950) }));
            // Carriers that ended by our end are history.
            Assert.Equal(900.0, GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200, new List<double> { 900 },
                new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(10, 150) }));
            // A carrier already running at our end carries the vessel itself: no hold.
            Assert.True(double.IsNaN(GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200, new List<double> { 900 },
                new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(150, 400) })));
            // No known takeover: never held open-ended.
            Assert.True(double.IsNaN(GhostCommNetMath.ResolveContinuationHoldUntilUT(
                200, new List<double> { 100 }, noCarriers)));
            Assert.True(double.IsNaN(GhostCommNetMath.ResolveContinuationHoldUntilUT(200, null, null)));
            Assert.True(double.IsNaN(GhostCommNetMath.ResolveContinuationHoldUntilUT(
                double.NaN, new List<double> { 900 }, null)));
        }

        [Theory]
        // chainWouldSpawn, superseded, spawnable, leaf, debris, ghostOnly, branch, spawnedOrDestroyed, expected
        [InlineData(true, false, false, false, false, false, false, false, true)]
        [InlineData(true, false, true, true, true, false, false, false, false)]
        [InlineData(true, false, true, true, false, false, false, true, false)]
        [InlineData(false, false, true, true, false, false, false, false, false)]
        [InlineData(false, true, true, true, false, false, false, false, true)]
        [InlineData(false, true, false, true, false, false, false, false, false)]
        [InlineData(false, true, true, false, false, false, false, false, false)]
        [InlineData(false, true, true, true, false, true, false, false, false)]
        [InlineData(false, true, true, true, false, false, true, false, false)]
        [InlineData(false, true, true, true, false, false, false, true, false)]
        public void ContinuationOwnsTerminalSpawn_Cases(
            bool chainWouldSpawn, bool superseded, bool spawnable, bool leaf, bool debris,
            bool ghostOnly, bool branch, bool spawnedOrDestroyed, bool expected)
        {
            Assert.Equal(expected, GhostCommNetMath.ContinuationOwnsTerminalSpawn(
                chainWouldSpawn, superseded, spawnable, leaf, debris, ghostOnly, branch, spawnedOrDestroyed));
        }

        [Fact]
        public void TryResolveContinuationHold_SupersededTip_HeldUntilLaterRecordingOfSameLaunch()
        {
            // The bdock-second-dock shape: mission A's tip (station, Orbiting) ends at 8950.61;
            // its terminal spawn is superseded by mission B's post-dock recording (11794.68).
            // Earlier recordings of the same launch are history; another launch of the same
            // craft (same baked pid, other guid) is not the vessel.
            Recording tipA = Rec("tipA", 42, GuidA, 8950.59, 8950.61);
            tipA.TerminalSpawnSupersededByRecordingId = "mergedB";
            var committed = new List<Recording>
            {
                Rec("launch", 42, GuidA, 26, 196, null),
                Rec("dockA", 42, GuidA, 8949.27, 8950.59, null),
                tipA,
                Rec("relaunch", 42, GuidB, 9000, 12000),
                Rec("mergedB", 42, GuidA, 11794.68, 11803.74),
            };
            var chain = new GhostChain { OriginalVesselPid = 42, LaunchGuid = GuidA };
            chain.Links.Add(new ChainLink { recordingId = "x", ut = 8949.3 });
            chain.Links.Add(new ChainLink { recordingId = "y", ut = 11794.68 });

            Assert.True(GhostCommNetMath.TryResolveContinuationHold(
                tipA, committed, chain, null, null, out double until, out string source));
            Assert.Equal(11794.68, until, 6);
            Assert.Equal("later-recording-of-vessel", source);

            // Without the chain the superseding recording alone still ends the hold.
            Assert.True(GhostCommNetMath.TryResolveContinuationHold(
                tipA, committed, null, null, null, out until, out source));
            Assert.Equal(11794.68, until, 6);
        }

        [Fact]
        public void TryResolveContinuationHold_DockMergeSuperseder_OtherPid_EndsAtItsStart()
        {
            // Dock-merge supersession: the continuation carries the SURVIVOR's pid, so it is
            // matched by id, not by launch identity.
            Recording rover = Rec("rover", 7, GuidA, 100, 200, TerminalState.Landed);
            rover.TerminalSpawnSupersededByRecordingId = "merged";
            var committed = new List<Recording> { rover, Rec("merged", 99, GuidB, 640, 900) };
            Assert.True(GhostCommNetMath.TryResolveContinuationHold(
                rover, committed, null, new List<double>(), new List<KeyValuePair<double, double>>(),
                out double until, out _));
            Assert.Equal(640.0, until, 6);
        }

        [Fact]
        public void TryResolveContinuationHold_ChainClaimBeforeAnyCarrier()
        {
            // A later dock (MERGE claim) folds the vessel into another vessel whose own
            // recording carries it: the hold ends at the claim, not at some later recording.
            Recording seg = Rec("seg", 42, GuidA, 100, 200);
            var committed = new List<Recording> { seg, Rec("later", 42, GuidA, 1500, 1600) };
            var chain = new GhostChain { OriginalVesselPid = 42, LaunchGuid = GuidA };
            chain.Links.Add(new ChainLink { recordingId = "c1", ut = 50 });
            chain.Links.Add(new ChainLink { recordingId = "c2", ut = 900 });
            Assert.True(GhostCommNetMath.TryResolveContinuationHold(
                seg, committed, chain, null, null, out double until, out string source));
            Assert.Equal(900.0, until, 6);
            Assert.Equal("ghost-chain-claim", source);
        }

        [Fact]
        public void ApplyContinuationHold_GatesAndLogs()
        {
            Recording tipA = Rec("applyTip", 42, GuidA, 100, 200);
            tipA.TerminalSpawnSupersededByRecordingId = "applyNext";
            var committed = new List<Recording> { tipA, Rec("applyNext", 42, GuidA, 800, 900) };

            var input = InWindowInput();
            GhostCommNetMath.ApplyContinuationHold(ref input, tipA, committed, null, false,
                r => false, "FLIGHT", null, null);
            Assert.True(input.HasContinuationHold);
            Assert.Equal(800.0, input.ContinuationHoldUntilUT, 6);
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]")
                && l.Contains("Continuation hold: key=applyTip") && l.Contains("scene=FLIGHT")
                && l.Contains("until=800.0") && l.Contains("takeover=later-recording-of-vessel"));

            // The real vessel carries its own stock node: never held.
            input = InWindowInput();
            GhostCommNetMath.ApplyContinuationHold(ref input, tipA, committed, null, false,
                r => true, "FLIGHT", null, null);
            Assert.False(input.HasContinuationHold);
            Assert.Contains(logLines, l => l.Contains("Continuation hold: key=applyTip")
                && l.Contains("real vessel exists"));

            // Already spawned: never held.
            tipA.VesselSpawned = true;
            input = InWindowInput();
            GhostCommNetMath.ApplyContinuationHold(ref input, tipA, committed, null, false,
                r => false, "TRACKSTATION", null, null);
            Assert.False(input.HasContinuationHold);

            // Neither chain-suppressed nor superseded: no hold and no log line.
            Recording plain = Rec("applyPlain", 43, GuidA, 100, 200);
            int before = logLines.Count;
            input = InWindowInput();
            input.HasContinuationHold = true;
            GhostCommNetMath.ApplyContinuationHold(ref input, plain, committed, null, false,
                r => false, "FLIGHT", null, null);
            Assert.False(input.HasContinuationHold);
            Assert.Equal(before, logLines.Count);
        }

        [Fact]
        public void ApplyContinuationHold_ChainIntermediateLink_UsesItsChainClaims()
        {
            // The walker's second intermediate-link clause: a recording of the chain's vessel
            // that ends before the chain's tip spawn.
            Recording seg = Rec("chainSeg", 42, GuidA, 100, 200);
            var chains = new Dictionary<uint, GhostChain>();
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                LaunchGuid = GuidA,
                SpawnUT = 5000,
                TipRecordingId = "tip",
            };
            chain.Links.Add(new ChainLink { recordingId = "c1", ut = 50 });
            chain.Links.Add(new ChainLink { recordingId = "c2", ut = 3000 });
            chains[42] = chain;
            Assert.Same(chain, GhostChainWalker.FindIntermediateLinkChain(chains, seg));
            Assert.True(GhostChainWalker.IsIntermediateChainLink(chains, seg));

            var input = InWindowInput();
            GhostCommNetMath.ApplyContinuationHold(ref input, seg, new List<Recording> { seg }, chains, true,
                r => false, "FLIGHT", null, null);
            Assert.True(input.HasContinuationHold);
            Assert.Equal(3000.0, input.ContinuationHoldUntilUT, 6);
        }

        [Fact]
        public void HoldPrediction_OnlyNearTheEnd()
        {
            double look = GhostCommNetMath.HoldPredictionLookaheadSeconds(0.25, 1.0);
            Assert.Equal(1.0, look, 9);
            Assert.Equal(500.0, GhostCommNetMath.HoldPredictionLookaheadSeconds(0.25, 1000.0), 9);
            Assert.Equal(1.0, GhostCommNetMath.HoldPredictionLookaheadSeconds(0.25, double.NaN), 9);
            Assert.True(GhostCommNetMath.ShouldPredictHoldPastEnd(199.5, 200, look));
            Assert.True(GhostCommNetMath.ShouldPredictHoldPastEnd(200, 200, look));
            Assert.False(GhostCommNetMath.ShouldPredictHoldPastEnd(150, 200, look));
            Assert.False(GhostCommNetMath.ShouldPredictHoldPastEnd(200.1, 200, look));
            Assert.False(GhostCommNetMath.ShouldPredictHoldPastEnd(double.NaN, 200, look));
        }

        [Fact]
        public void RegistrationPass_Precedence()
        {
            Assert.Equal(1, GhostCommNetMath.RegistrationPass(false, "in-window"));
            Assert.Equal(1, GhostCommNetMath.RegistrationPass(false, "held-for-spawn"));
            Assert.Equal(1, GhostCommNetMath.RegistrationPass(false, "chain-gap-hold"));
            Assert.Equal(1, GhostCommNetMath.RegistrationPass(false, "window-ended"));
            Assert.Equal(2, GhostCommNetMath.RegistrationPass(true, "chain-pre-claim"));
            Assert.Equal(2, GhostCommNetMath.RegistrationPass(true, "continuation-gap-hold"));
            Assert.Equal(3, GhostCommNetMath.RegistrationPass(false, "continuation-gap-hold"));
        }

        // ---------------------------------------------------------------- logging

        [Fact]
        public void LogRegisteredAndRemoved_InvariantCulture()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var t = GhostCommNetMath.BuildTimeline(RelaySatSpec(), null);
                GhostCommNetMath.LogRegistered("rec1", "ParsekGhost:rec1", "Relay Sat", "FLIGHT", "in-window",
                    1234.5, 1000, 2000, false, false, t.Sample(1234.5), 1.0);
                GhostCommNetMath.LogRemoved("rec1", "Relay Sat", "FLIGHT", "vessel-spawned", 2000.25);
                GhostCommNetMath.LogStateChange("rec1", "Relay Sat", 1500, t.Sample(1500), 0.5);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]")
                && l.Contains("Registered ghost node: key=rec1 node=ParsekGhost:rec1 vessel=\"Relay Sat\" scene=FLIGHT")
                && l.Contains("ut=1234.5") && l.Contains("window=[1000.0,2000.0]")
                && l.Contains("relay=2000000000 transmit=5000") && l.Contains("control=True multiHop=True"));
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]")
                && l.Contains("Removed ghost node: key=rec1") && l.Contains("reason=vessel-spawned")
                && l.Contains("ut=2000.3"));
            Assert.Contains(logLines, l => l.Contains("Ghost node state change: key=rec1")
                && l.Contains("relay=1000000000 transmit=2500"));
        }

        [Fact]
        public void LogAvailability_OneLinePerOutcome()
        {
            GhostCommNetMath.LogAvailability("TRACKSTATION", GhostCommNetAvailability.CommNetDisabled, null);
            GhostCommNetMath.LogAvailability("FLIGHT", GhostCommNetAvailability.ForeignNetworkTypes, "network=X");
            GhostCommNetMath.LogAvailability("FLIGHT", GhostCommNetAvailability.Available, null);
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]") && l.Contains("CommNet is off in scene TRACKSTATION")
                && l.Contains("RemoteTech"));
            Assert.Contains(logLines, l => l.Contains("not stock in scene FLIGHT (network=X)")
                && l.Contains("RealAntennas"));
            Assert.Contains(logLines, l => l.Contains("Stock CommNet available in scene FLIGHT"));
        }

        [Fact]
        public void LogDerived_SummarizesCounts()
        {
            var spec = RelaySatSpec();
            spec.PartsMissingPrefab = 1;
            spec.UnknownCrew = 2;
            spec.SnapshotSource = "vessel-snapshot";
            GhostCommNetMath.LogDerived("rec9", spec, GhostCommNetMath.BuildTimeline(spec, null));
            Assert.Contains(logLines, l => l.Contains("[GhostCommNet]")
                && l.Contains("Derived antennas: key=rec9 vessel=\"Relay Sat\" source=vessel-snapshot type=Relay parts=4")
                && l.Contains("relayAntennas=2 otherAntennas=1 deployGated=1 controlPoints=1")
                && l.Contains("pilots=1 unknownCrew=2 partsMissingPrefab=1")
                && l.Contains("capable=True"));
        }
    }
}
