using System;
using System.Collections.Generic;
using Xunit;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Re-aim Phase 4, implementation Phase 3b: the PURE arrival-hold planner
    // (ArrivalHoldPlanner.ComputeArrivalHold) the builder calls to decide the loop-clock hold for a re-aim
    // landing. Gated cases return None (fail closed => byte-identical clock); a landing that needs alignment
    // returns the minimal forward hold. No engine wiring exercised here.
    public class ArrivalHoldPlannerTests
    {
        private static PhaseConstraint Rotation(string body, double period = 65000.0)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = 1.0e7, PhaseOffsetSeconds = 0.0, RelativeToParent = true };

        // Duna rotation = dunaRot (default 100); Laythe/Vall are moons of Jool; Duna/Jool are Sun children.
        private sealed class HoldFake : IBodyInfo
        {
            private readonly double dunaRot;
            public HoldFake(double dunaRot = 100.0) { this.dunaRot = dunaRot; }
            public double RotationPeriod(string b) => b == "Duna" ? dunaRot : (b == "Jool" ? 36000.0 : double.NaN);
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) =>
                (b == "Duna" || b == "Jool") ? "Sun" : ((b == "Laythe" || b == "Vall") ? "Jool" : null);
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid, out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private static readonly List<PhaseConstraint> DunaLanding =
            new List<PhaseConstraint> { Rotation("Duna"), Orbital("Duna") };

        [Fact]
        public void Off_Drop_ReturnsNone()
        {
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Drop, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.Equal(0.0, r.HoldSeconds);
        }

        [Fact]
        public void OrbitOnly_NoLandingRotation_ReturnsNone()
        {
            // No Rotation(Duna) -> orbit-only arrival -> nothing to align.
            var orbitOnly = new List<PhaseConstraint> { Orbital("Duna") };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                orbitOnly, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void TwoConstrainedMoons_NoWindowSpacing_DeclinesWithAmber()
        {
            // M-MIS-6 (supersedes the pre-M-MIS-6 fail-closed pin): the 2+-moon shape now routes
            // through the multi-moon configuration hold, whose window gate needs a valid synodic
            // spacing - omitted here, so the shape declines EXPLICITLY with an amber (the config
            // hold is never silent; the engage/decline fixtures live in MultiMoonAlignmentTests).
            var jool = new List<PhaseConstraint>
            {
                Rotation("Jool"), Orbital("Jool"), Orbital("Laythe"), Orbital("Vall"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                jool, "Jool", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
        }

        [Fact]
        public void DegenerateRotationPeriod_ReturnsNone()
        {
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new HoldFake(double.NaN));
            Assert.False(r.Applied);
        }

        [Fact]
        public void Landing_NeedsHold_AppliedWithMinimalForwardDelay()
        {
            // recordedArrival 1000, phaseAnchor 350, span start 0, no cuts -> liveEntry = 350 + 1000 = 1350.
            // W = (1000 - 1350) mod 100 = 50. After the hold, entry replays at 1400 == 1000 (mod 100): aligned.
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake(100.0));
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.Equal(1000.0, r.HoldAtUT, 9);
        }

        [Fact]
        public void Landing_AlreadyAligned_ReturnsNone()
        {
            // phaseAnchor 0 -> liveEntry = 1000 == recordedArrival (mod 100): already aligned, no hold.
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 0.0, 0.0, null, new HoldFake(100.0));
            Assert.False(r.Applied);
            Assert.Equal(0.0, r.HoldSeconds);
        }

        [Fact]
        public void Landing_AccountsForLaunchSideLoiterCompression()
        {
            // A launch-side cut [200,300] (excise 100) compresses the entry's live time: liveEntry =
            // 350 + (CompressSpanUT(1000) = 900) = 1250 -> W = (1000 - 1250) mod 100 = 50.
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 200.0, LengthSeconds = 100.0 },
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Tight, 350.0, 0.0, cuts, new HoldFake(100.0));
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.Equal(1000.0, r.HoldAtUT, 9);
        }

        [Fact]
        public void Landing_WithDestinationSideLoiterCut_ReturnsNone()
        {
            // A cut AFTER the SOI entry (a recorded destination parking orbit) breaks the entry->deorbit
            // rigidity, so the hold fails closed (deferred to the L8 pre-landing trim). A launch-side cut
            // (before entry) does NOT trip this - see Landing_AccountsForLaunchSideLoiterCompression.
            var destCut = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1100.0, LengthSeconds = 200.0 },  // after arrival 1000
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, destCut, new HoldFake(100.0));
            Assert.False(r.Applied);
        }

        // === M4c (Tier 2): the destination-station hold + D8 =================================

        private static PhaseConstraint Station(string orbitedBody, double period = 100.0, uint pid = 4242)
            => new PhaseConstraint { Kind = ConstraintKind.VesselOrbital, BodyName = orbitedBody,
                PeriodSeconds = period, PhaseOffsetSeconds = 650.0, AnchorVesselPid = pid };

        [Fact]
        public void StationOnly_HoldComputedFromStationPeriod()
        {
            // M4c plan test 9: a station-only destination holds on T_station, NOT the body's
            // rotation period - the fake's Duna rotation is poisoned to 999999 so any T_rot leak
            // produces a wildly different hold. Same geometry as the landing test: liveEntry =
            // 350 + 1000 = 1350, W = (1000 - 1350) mod 100 = 50.
            var stationOnly = new List<PhaseConstraint> { Orbital("Duna"), Station("Duna", period: 100.0) };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                stationOnly, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new HoldFake(999999.0));
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.Equal(1000.0, r.HoldAtUT, 9);
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);
            Assert.True(r.IsStationHold);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void Landing_CarriesRotationAlignPeriod_NotStationKind()
        {
            // The landing hold's result discriminators: align period = T_rot, IsStationHold false.
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                DunaLanding, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake(100.0));
            Assert.True(r.Applied);
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);
            Assert.False(r.IsStationHold);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_TidalPeriods_DegeneratesToStationHold()
        {
            // Post-M4c joint wiring: with T_rot == T_station (the tidal-degenerate pair) aligning
            // the station aligns the rotation for free, so the joint branch degenerates to the
            // plain station hold - no joint fields, no amber. (This input was the old
            // DualLandingPlusStation_D8_NoneWithAmber fixture; D8's fail-closed letter for shape
            // (a) is superseded by the SolveArrivalWindow wiring.)
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 100.0), Orbital("Duna"), Station("Duna"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake(100.0));
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);
            Assert.True(r.IsStationHold);
            Assert.False(r.IsJointHold);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_JointEngages_WhenLatticeFeasible()
        {
            // The headline post-M4c shape (a): landing rotation (T_rot 360, Loose tolerance 5s)
            // + station (T_station 100). The station-lattice orbit i*100 mod 360 hits the
            // rotation tolerance every 18th whole period (worst miss-run 17, well inside the 64
            // budget), so the joint hold ENGAGES: station-exact base hold (same 50s geometry as
            // the single-period tests), joint fields carrying T_rot + its tolerance + the budget.
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 360.0), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new HoldFake(360.0),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.True(r.Applied);
            Assert.True(r.IsJointHold);
            Assert.True(r.IsStationHold);
            Assert.Equal(50.0, r.HoldSeconds, 9);              // station-lattice base, clock adds i_N
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);      // station exact
            Assert.Equal(360.0, r.JointSecondaryPeriodSeconds, 9);
            Assert.Equal(360.0 * 5.0 / 360.0, r.JointSecondaryToleranceSeconds, 9); // Loose 5 deg
            Assert.Equal(DestinationArrivalSolver.MaxJointHoldWholePeriods, r.JointMaxWholeHoldPeriods);
            Assert.True(r.JointChosenWindowK >= 1);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_ToleranceEdge_LooseEngages_TightAmbers()
        {
            // The tolerance-edge decline, one lever: tSta=100 / tRot=3617.7 (incommensurate).
            // Under Loose (5 deg -> 50.25s) the lattice's worst miss-run is 36 <= 64 -> engage;
            // under Tight (0.25 deg -> 2.51s) it is 1627 > 64 -> fail closed with the amber
            // naming the tolerance miss. Never a silent partial alignment.
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 3617.7), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var loose = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new HoldFake(3617.7),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.True(loose.Applied);
            Assert.True(loose.IsJointHold);

            var tight = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Tight, 350.0, 0.0, null,
                new HoldFake(3617.7),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.False(tight.Applied);
            Assert.Equal(0.0, tight.HoldSeconds);
            Assert.Contains("misses tolerance", tight.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_IncommensuratePastBudget_NoneWithAmber()
        {
            // The fail-closed fixture: incommensurate periods whose joint lattice never reaches
            // the Tight rotation tolerance within the whole-period budget (worst miss-run 1627 vs
            // budget 64) - no hold, amber names the lattice miss + the budget.
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 3617.7), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Tight, 350.0, 0.0, null,
                new HoldFake(3617.7),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.False(r.Applied);
            Assert.Contains("whole station periods", r.AmberReason);
            Assert.Contains("budget", r.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_NoSlackBudget_NoneWithAmber()
        {
            // The builder-measured loop slack clamps the whole-period budget; a slack smaller
            // than two station periods leaves no room for any joint extension - amber, honest.
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 360.0), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new HoldFake(360.0),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin", loopSlackSeconds: 150.0);
            Assert.False(r.Applied);
            Assert.Contains("loop slack", r.AmberReason);
        }

        [Fact]
        public void DualLandingPlusStation_DestinationSideCut_NoneWithAmber()
        {
            // The L8 rigidity guard on the joint shape: a destination-side cut breaks the
            // entry-referenced joint hold - amber (a station is involved), never a partial hold.
            var destCut = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1100.0, LengthSeconds = 200.0 },
            };
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 360.0), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, destCut,
                new HoldFake(360.0),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.False(r.Applied);
            Assert.Contains("destination-side loiter cut", r.AmberReason);
        }

        [Fact]
        public void JoolClassNoStation_DeclineIsNeverSilent()
        {
            // M-MIS-6 (supersedes M4c plan test 10b): the no-station Jool-class decline is no
            // longer silent - the multi-moon configuration hold owns the shape and every decline
            // carries an amber reason (design D6). The engage polarity lives in
            // MultiMoonAlignmentTests.
            var jool = new List<PhaseConstraint>
            {
                Rotation("Jool"), Orbital("Jool"), Orbital("Laythe"), Orbital("Vall"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                jool, "Jool", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
        }

        [Fact]
        public void StationPlusConstrainedMoon_NoneWithAmber()
        {
            // M4c plan test 10c (the D8 widening): station + one constrained moon - None + amber.
            var stationPlusMoon = new List<PhaseConstraint>
            {
                Orbital("Jool"), Orbital("Laythe"), Station("Jool"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                stationPlusMoon, "Jool", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.Contains("station+moon", r.AmberReason);
        }

        [Fact]
        public void MoonOrbitingStation_NoneWithAmber()
        {
            // A station orbiting a MOON of the target (a Laythe depot on a Jool mission): the
            // in-system shape the hold cannot align - None + the in-system amber.
            var moonStation = new List<PhaseConstraint>
            {
                Orbital("Jool"), Station("Laythe"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                moonStation, "Jool", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.Contains("moon-orbiting station stays fail-closed", r.AmberReason);
        }

        [Fact]
        public void Drop_StationOnly_HoldStillComputed()
        {
            // M4c plan test 12a: the Drop gate is ROTATION-scoped (the transited-body rotation
            // alignment A/B); the station hold is a fully automatic alignment with no toggle
            // (design D4), so it computes under Drop too.
            var stationOnly = new List<PhaseConstraint> { Orbital("Duna"), Station("Duna", period: 100.0) };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                stationOnly, "Duna", 1000.0, TransitedBodyRotationMode.Drop, 350.0, 0.0, null, new HoldFake());
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.True(r.IsStationHold);
        }

        [Fact]
        public void Drop_DualLandingPlusStation_DegeneratesToStationHold()
        {
            // Post-M4c joint wiring (supersedes the M4c "Drop does not rescue the dual" pin): the
            // SolveArrivalWindow wiring makes shape (a) mode-aware exactly as the M4c deferral
            // anticipated - under Drop the rotation alignment is the player's explicit "Off", so
            // the dual degrades to the plain station hold (station-exact, no joint fields).
            var dual = new List<PhaseConstraint>
            {
                Rotation("Duna", period: 360.0), Orbital("Duna"), Station("Duna", period: 100.0),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                dual, "Duna", 1000.0, TransitedBodyRotationMode.Drop, 350.0, 0.0, null, new HoldFake(),
                windowSpacingSeconds: 12345.0, launchBodyName: "Kerbin");
            Assert.True(r.Applied);
            Assert.Equal(50.0, r.HoldSeconds, 9);
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);
            Assert.True(r.IsStationHold);
            Assert.False(r.IsJointHold);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void DegenerateStationPeriod_ReturnsNone()
        {
            // M4c plan test 12c: a NaN / zero / negative station period fails closed silently.
            foreach (double bad in new[] { double.NaN, 0.0, -100.0 })
            {
                var stationOnly = new List<PhaseConstraint> { Orbital("Duna"), Station("Duna", period: bad) };
                var r = ArrivalHoldPlanner.ComputeArrivalHold(
                    stationOnly, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
                Assert.False(r.Applied);
                Assert.Null(r.AmberReason);
            }
        }

        [Fact]
        public void Station_WithDestinationSideLoiterCut_ReturnsNone()
        {
            // M4c plan test 11: the L8 rigidity guard covers the station kind - a cut after the
            // SOI entry breaks the entry-referenced hold regardless of which period it aligns.
            var destCut = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1100.0, LengthSeconds = 200.0 },
            };
            var stationOnly = new List<PhaseConstraint> { Orbital("Duna"), Station("Duna", period: 100.0) };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                stationOnly, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, destCut, new HoldFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void LaunchSideStation_OrbitOnlyArrival_ReturnsNoneNoAmber()
        {
            // A Kerbin fuel depot on a Duna re-aim: not a destination constraint - with no
            // landing and no destination station the arrival is orbit-only faithful, no amber.
            var launchSide = new List<PhaseConstraint> { Orbital("Duna"), Station("Kerbin") };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                launchSide, "Duna", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
            Assert.Null(r.AmberReason);
        }
    }
}
