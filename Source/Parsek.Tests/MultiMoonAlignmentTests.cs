using System;
using System.Collections.Generic;
using Xunit;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // M-MIS-6 (docs/dev/design-mission-multimoon-alignment.md): the multi-moon destination
    // T_config hold - flipping the DestinationConstraintExtractor 2+-moon fail-closed into a
    // single per-loop hold on the joint configuration period. These fixtures were written and
    // verified FAILING before any implementation (the re-aim failing-test-first discipline):
    // against the pre-M-MIS-6 code the extractor fails closed at 2+ SOI-entered moons, so the
    // engage fixtures see Applied=false and the decline fixtures see a SILENT None (no amber).
    //
    // The synthetic system is the stock Jool pack (periods from the stock SMAs, SOI radii and
    // orbital velocities as shipped), pinned as constants so every assertion is deterministic:
    // Laythe:Vall:Tylo are the near-exact 1:2:4 resonance (P_Vall - 2*P_Laythe = +0.3s,
    // P_Tylo - 4*P_Laythe = +2.8s), Bop is incommensurate. The expected T_config is 2*P_Vall
    // (~= T_Tylo ~= 211,924 s): the smallest-duty participant (Vall) anchors the lattice.
    public class MultiMoonAlignmentTests
    {
        // Stock Jool-system values (computed from the stock SMAs, mu_Jool = 2.82528e14).
        private const double LaytheOrbit = 52980.9;
        private const double VallOrbit = 105962.1;
        private const double TyloOrbit = 211926.4;
        private const double BopOrbit = 544507.4;
        private const double TConfig = 2.0 * VallOrbit;          // 211,924.2 s (~= T_Tylo)
        private const double KerbinJoolSynodic = 10090902.0;      // ~1.10 Kerbin years

        private static PhaseConstraint Rotation(string body, double period)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period = 1.0e7)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = true };

        private static PhaseConstraint Station(string orbitedBody, double period = 1800.0)
            => new PhaseConstraint { Kind = ConstraintKind.VesselOrbital, BodyName = orbitedBody,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, AnchorVesselPid = 4242 };

        // Stock-like Jool system: SOI radii + orbital velocities drive the Orbital constraint
        // tolerance (SoiRadius/OrbitalVelocity): Laythe 1155.0s, Vall 940.4s, Tylo 5345.7s,
        // Bop 823.5s. The major moons are tidally locked (rotation == orbit period).
        private sealed class JoolFake : IBodyInfo
        {
            public double RotationPeriod(string b)
            {
                switch (b)
                {
                    case "Jool": return 36000.0;
                    case "Laythe": return LaytheOrbit;
                    case "Vall": return VallOrbit;
                    case "Tylo": return TyloOrbit;
                    case "Bop": return BopOrbit;
                    default: return double.NaN;
                }
            }
            public double OrbitPeriod(string b)
            {
                switch (b)
                {
                    case "Laythe": return LaytheOrbit;
                    case "Vall": return VallOrbit;
                    case "Tylo": return TyloOrbit;
                    case "Bop": return BopOrbit;
                    default: return double.NaN;
                }
            }
            public string ReferenceBodyName(string b) =>
                b == "Jool" ? "Sun"
                : (b == "Laythe" || b == "Vall" || b == "Tylo" || b == "Bop") ? "Jool"
                : null;
            public double SoiRadius(string b)
            {
                switch (b)
                {
                    case "Laythe": return 3723645.8;
                    case "Vall": return 2406401.4;
                    case "Tylo": return 10856518.0;
                    case "Bop": return 1221060.9;
                    default: return double.NaN;
                }
            }
            public double OrbitalVelocity(string b)
            {
                switch (b)
                {
                    case "Laythe": return 3223.8;
                    case "Vall": return 2558.8;
                    case "Tylo": return 2030.9;
                    case "Bop": return 1482.8;
                    default: return double.NaN;
                }
            }
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        // The resonant inner-three tour: Jool SOI entry (the target's own Orbital, excluded by
        // the extractor) + Laythe/Vall/Tylo SOI entries. No Jool landing, no station.
        private static List<PhaseConstraint> InnerThreeTour() => new List<PhaseConstraint>
        {
            Orbital("Jool", 1.0e7),
            Orbital("Laythe", LaytheOrbit),
            Orbital("Vall", VallOrbit),
            Orbital("Tylo", TyloOrbit),
        };

        private static ArrivalHoldPlanner.ArrivalHoldResult Compute(
            List<PhaseConstraint> constraints,
            TransitedBodyRotationMode mode = TransitedBodyRotationMode.Loose,
            double loopSlackSeconds = 5.0e6,
            double phaseAnchorUT = 350.0)
        {
            return ArrivalHoldPlanner.ComputeArrivalHold(
                constraints, "Jool", 1.0e6, mode, phaseAnchorUT, 0.0, null, new JoolFake(),
                windowSpacingSeconds: KerbinJoolSynodic,
                launchBodyName: "Kerbin",
                loopSlackSeconds: loopSlackSeconds);
        }

        // === Fixture A: the resonant inner-three tour engages the T_config hold ==============

        [Fact]
        public void ResonantInnerThree_EngagesConfigHold_OnJointConfigurationPeriod()
        {
            var r = Compute(InnerThreeTour());
            Assert.True(r.Applied, "the resonant inner-three tour must engage the config hold");
            Assert.False(r.IsStationHold);
            Assert.False(r.IsJointHold);
            Assert.Null(r.AmberReason);
            // T_config = 2*P_Vall (the smallest-duty anchor's lattice), ~= T_Tylo as investigated.
            Assert.Equal(TConfig, r.AlignPeriodSeconds, 3);
            Assert.True(r.HoldSeconds > 0.0 && r.HoldSeconds <= TConfig + 1e-9,
                $"hold must lie in (0, T_config], got {r.HoldSeconds}");
            Assert.Equal(1.0e6, r.HoldAtUT, 9);
        }

        [Fact]
        public void ResonantInnerThree_PerLoopHold_AlignsEveryEncounterWithinSoiTolerance()
        {
            // The encounter-alignment property (the ArrivalAlignHoldTests re-derivation pattern):
            // with the single-period per-loop hold on T_config, the total live shift of every
            // replayed loop N sits within each moon's SOI tolerance of a whole number of that
            // moon's periods - i.e. every moon is where the recording had it at every encounter,
            // within SOI-crossing time. Swept across the aligned horizon the design commits to.
            var r = Compute(InnerThreeTour());
            Assert.True(r.Applied);

            double cadence = KerbinJoolSynodic;
            double entryOffset0 = 350.0; // phaseAnchorUT - spanStartUT, no loiter cuts
            var tolerances = new (string body, double period, double tol)[]
            {
                ("Laythe", LaytheOrbit, 3723645.8 / 3223.8),
                ("Vall", VallOrbit, 2406401.4 / 2558.8),
                ("Tylo", TyloOrbit, 10856518.0 / 2030.9),
            };
            for (long n = 0; n <= 12; n++)
            {
                double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(
                    r.HoldSeconds, n, cadence, r.AlignPeriodSeconds);
                Assert.InRange(wN, 0.0, r.AlignPeriodSeconds + 1e-6);
                double shift = entryOffset0 + n * cadence + wN;
                foreach (var (body, period, tol) in tolerances)
                {
                    double err = MissionPeriodicity.CircularPhaseError(shift, period);
                    Assert.True(err <= tol,
                        $"loop {n}: {body} phase error {err:F1}s exceeds SOI tolerance {tol:F1}s");
                }
            }
        }

        [Fact]
        public void ResonantInnerThree_TidallyLockedMoonLanding_StillEngages()
        {
            // Finding (c): a landing on a tidally locked moon adds a Rotation constraint whose
            // period EQUALS the moon's orbital period, so it collapses into the orbital phase
            // and the config hold still engages (no extra period, no decline).
            var tour = InnerThreeTour();
            tour.Add(Rotation("Laythe", LaytheOrbit)); // tidally locked: rotation == orbit
            var r = Compute(tour);
            Assert.True(r.Applied,
                "a tidally locked moon landing must collapse into the orbital constraint");
            Assert.Equal(TConfig, r.AlignPeriodSeconds, 3);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void Drop_MultiMoon_StillAlignsOrbitalConfiguration()
        {
            // The Drop gate is ROTATION-scoped (the transited-body rotation alignment A/B);
            // moon ORBITAL constraints are never dropped, so the configuration hold still
            // engages under Drop even with landing rotations present.
            var tour = InnerThreeTour();
            tour.Add(Rotation("Jool", 36000.0));
            tour.Add(Rotation("Laythe", LaytheOrbit));
            var r = Compute(tour, mode: TransitedBodyRotationMode.Drop);
            Assert.True(r.Applied);
            Assert.Equal(TConfig, r.AlignPeriodSeconds, 3);
            Assert.Null(r.AmberReason);
        }

        // === Fixture B: incommensurate participants fail closed with amber, never silent ======

        [Fact]
        public void IncommensurateBopMoon_FailsClosedWithAmberNamingTheShape()
        {
            // Finding (d): Bop is incommensurate with the inner three - the joint configuration
            // of all four moons is effectively non-recurring, so the whole config fails closed
            // to faithful WITH an amber reason (the pre-M-MIS-6 silent None is the bug).
            var tour = InnerThreeTour();
            tour.Add(Orbital("Bop", BopOrbit));
            var r = Compute(tour);
            Assert.False(r.Applied);
            Assert.Equal(0.0, r.HoldSeconds);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("does not recur", r.AmberReason);
            Assert.Contains("Bop", r.AmberReason);
        }

        [Fact]
        public void NonLockedMoonRotationLanding_FailsClosedWithAmber()
        {
            // A landing on a NON-tidally-locked moon (rotation period incommensurate with the
            // pack) adds a period the joint recurrence cannot satisfy: honest amber decline.
            var tour = InnerThreeTour();
            tour.Add(Rotation("Laythe", 40000.0)); // NOT the 52980.9s orbit period
            var r = Compute(tour);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("does not recur", r.AmberReason);
        }

        [Fact]
        public void JoolLandingRotation_MultiMoon_FailsClosedWithAmber()
        {
            // D5's one-hold-one-period honesty: a target-body landing rotation (36,000s) is
            // incommensurate with the moon pack's T_config lattice, so the shape declines under
            // Loose (under Drop the rotation is removed and the moons align - see the Drop test).
            var tour = InnerThreeTour();
            tour.Add(Rotation("Jool", 36000.0));
            var r = Compute(tour, mode: TransitedBodyRotationMode.Loose);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("does not recur", r.AmberReason);
        }

        [Fact]
        public void SlackSmallerThanAnchorPeriod_FailsClosedWithAmber()
        {
            // The clock's defensive hold clamp must never silently truncate the config hold:
            // a loop slack below one anchor period leaves no whole-configuration-period budget.
            var r = Compute(InnerThreeTour(), loopSlackSeconds: 100000.0);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("loop slack", r.AmberReason);
        }

        [Fact]
        public void MultiMoon_DestinationSideLoiterCut_FailsClosedWithAmber()
        {
            // The L8 rigidity guard covers the config hold: a cut excised AFTER the SOI entry
            // breaks the entry-referenced rigidity that lets one hold align every encounter.
            var destCut = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1.1e6, LengthSeconds = 200.0 },
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                InnerThreeTour(), "Jool", 1.0e6, TransitedBodyRotationMode.Loose, 350.0, 0.0,
                destCut, new JoolFake(),
                windowSpacingSeconds: KerbinJoolSynodic,
                launchBodyName: "Kerbin",
                loopSlackSeconds: 5.0e6);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("loiter cut", r.AmberReason);
        }

        // === Extractor: the 2+-moon shape EMITS instead of rejecting =========================

        [Fact]
        public void Extractor_MultiMoon_EmitsSupportedConstraintSet()
        {
            var set = DestinationConstraintExtractor.ExtractDestinationConstraints(
                InnerThreeTour(), "Jool", new JoolFake());
            Assert.True(set.Supported,
                "2+ constrained moons must emit a supported multi-moon set, not fail closed");
            Assert.Equal(3, set.ConstrainedMoonCount);
            Assert.Equal(3, set.Constraints.Count); // the three MoonConfigs, no DestRotation
            Assert.All(set.Constraints, c => Assert.Equal(ConstraintKind.Orbital, c.Kind));
            Assert.False(set.HasStation);
            Assert.False(set.IsJointLandingStation);
        }

        [Fact]
        public void Extractor_MultiMoonWithStation_StaysFailClosed_WithStationMoonReason()
        {
            // Station-bearing multi-moon shapes stay fail-closed (D5): the joint solve models
            // one exact lattice + one checked period; a station on top of a moon pack is 3+
            // coupled periods. With the moon-count early-return gone, the station+moon reject
            // owns the shape and its reason surfaces (the old fixture asserted the moon-count
            // reason shadowed it).
            var tour = InnerThreeTour();
            tour.Add(Station("Jool"));
            var set = DestinationConstraintExtractor.ExtractDestinationConstraints(
                tour, "Jool", new JoolFake());
            Assert.False(set.Supported);
            Assert.Contains("station", set.Reason);
            Assert.True(set.HasStation);
        }

        // === Byte-identity pin: the shipped single-moon shape is untouched ===================

        [Fact]
        public void SingleMoon_TidallyLockedIke_KeepsPlainRotationHold()
        {
            // The shipped Duna+Ike shape (1 constrained moon): the plain destination-rotation
            // hold on T_rot(Duna), NOT a config hold. Passes before AND after M-MIS-6 -
            // pins that the multi-moon branch engages only at 2+ constrained moons.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Duna", 65517.86),
                Orbital("Duna", 1.0e7),
                Orbital("Ike", 65517.86),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                constraints, "Duna", 1.0e6, TransitedBodyRotationMode.Loose, 350.0, 0.0, null,
                new DunaIkeFake(),
                windowSpacingSeconds: KerbinJoolSynodic,
                launchBodyName: "Kerbin",
                loopSlackSeconds: 5.0e6);
            Assert.True(r.Applied);
            Assert.False(r.IsStationHold);
            Assert.False(r.IsJointHold);
            Assert.Equal(65517.86, r.AlignPeriodSeconds, 6); // T_rot(Duna), the shipped model
            Assert.Null(r.AmberReason);
        }

        private sealed class DunaIkeFake : IBodyInfo
        {
            public double RotationPeriod(string b) => b == "Duna" ? 65517.86 : double.NaN;
            public double OrbitPeriod(string b) => b == "Ike" ? 65517.86 : double.NaN;
            public string ReferenceBodyName(string b) =>
                b == "Duna" ? "Sun" : (b == "Ike" ? "Duna" : null);
            public double SoiRadius(string b) => b == "Ike" ? 1049598.9 : double.NaN;
            public double OrbitalVelocity(string b) => b == "Ike" ? 316.1 : double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }
    }
}
