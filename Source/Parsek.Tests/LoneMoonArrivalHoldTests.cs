using System;
using System.Collections.Generic;
using Xunit;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // The lone-moon arrival hold: a looped interplanetary trip that goes on to ONE moon of the
    // destination planet (Kerbin -> Duna -> Ike orbit or Ike landing) with no landing on the
    // planet itself. The periodicity design (Phase 4, the L0-L9 chain) names the moon's orbital
    // phase as an arrival constraint; before this change ArrivalHoldPlanner's single-period pick
    // returned None for that shape, so the moon-relative replay detached from the approach. The
    // shape now routes through the moon configuration hold (ComputeMultiMoonConfigHold with one
    // participant set), and the builder keeps the destination loiter whole so the hold stays
    // valid. A landing on the planet with its rotation hold in force keeps the shipped rotation
    // hold (MultiMoonAlignmentTests.SingleMoon_TidallyLockedIke_KeepsPlainRotationHold).
    [Collection("Sequential")]
    public class LoneMoonArrivalHoldTests : IDisposable
    {
        private const double IkeOrbit = 65517.86;
        private const double KerbinDunaSynodic = 19645697.0; // ~2.1 Kerbin years
        private const double IkeSoiTolerance = 1049598.9 / 316.1;

        private readonly List<string> logLines = new List<string>();

        public LoneMoonArrivalHoldTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionLoopUnitBuilder.SuppressLogging = false;
            MissionPeriodicity.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionLoopUnitBuilder.SuppressLogging = false;
            MissionPeriodicity.SuppressLogging = false;
        }

        private static PhaseConstraint Rotation(string body, double period)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period = 1.0e7)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = true };

        private static PhaseConstraint Station(string orbitedBody, double period = 1800.0)
            => new PhaseConstraint { Kind = ConstraintKind.VesselOrbital, BodyName = orbitedBody,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, AnchorVesselPid = 4242 };

        // Stock-like Duna system plus a synthetic non-tidally-locked moon ("Slowmoon") whose
        // rotation is incommensurate with its orbit, to drive the honest decline.
        private sealed class DunaFake : IBodyInfo
        {
            public double RotationPeriod(string b) =>
                b == "Duna" ? IkeOrbit : b == "Ike" ? IkeOrbit : b == "Slowmoon" ? 28255.0 : double.NaN;
            public double OrbitPeriod(string b) =>
                b == "Ike" ? IkeOrbit : b == "Slowmoon" ? 388587.8 : double.NaN;
            public string ReferenceBodyName(string b) =>
                b == "Duna" ? "Sun" : (b == "Ike" || b == "Slowmoon") ? "Duna" : null;
            public double SoiRadius(string b) =>
                b == "Ike" ? 1049598.9 : b == "Slowmoon" ? 126123.3 : double.NaN;
            public double OrbitalVelocity(string b) =>
                b == "Ike" ? 316.1 : b == "Slowmoon" ? 274.0 : double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private static ArrivalHoldPlanner.ArrivalHoldResult Compute(
            List<PhaseConstraint> constraints,
            TransitedBodyRotationMode mode = TransitedBodyRotationMode.Loose,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> cuts = null,
            double loopSlackSeconds = 1.0e7)
        {
            return ArrivalHoldPlanner.ComputeArrivalHold(
                constraints, "Duna", 1.0e6, mode, 350.0, 0.0, cuts, new DunaFake(),
                windowSpacingSeconds: KerbinDunaSynodic,
                launchBodyName: "Kerbin",
                loopSlackSeconds: loopSlackSeconds);
        }

        // Kerbin pad launch -> Duna SOI entry -> Ike SOI entry; no Duna landing, no station.
        private static List<PhaseConstraint> IkeOrbitTrip() => new List<PhaseConstraint>
        {
            Rotation("Kerbin", 21549.4),
            Orbital("Duna", 1.0e7),
            Orbital("Ike", IkeOrbit),
        };

        // === The predicate ===================================================================

        [Fact]
        public void Predicate_TruthTable()
        {
            var fake = new DunaFake();
            DestinationConstraintExtractor.DestinationConstraintSet Set(params PhaseConstraint[] cs)
                => DestinationConstraintExtractor.ExtractDestinationConstraints(
                    new List<PhaseConstraint>(cs), "Duna", fake);

            var loose = TransitedBodyRotationMode.Loose;
            var drop = TransitedBodyRotationMode.Drop;
            // Lone moon, no planet landing: moon hold.
            Assert.True(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Orbital("Duna"), Orbital("Ike", IkeOrbit)), loose));
            // Lone moon + moon landing: moon hold.
            Assert.True(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Orbital("Duna"), Orbital("Ike", IkeOrbit), Rotation("Ike", IkeOrbit)), loose));
            // Planet landing with the rotation hold in force: the shipped rotation hold.
            Assert.False(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Rotation("Duna", IkeOrbit), Orbital("Duna"), Orbital("Ike", IkeOrbit)), loose));
            // Planet landing with alignment Off: the moon is still aligned.
            Assert.True(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Rotation("Duna", IkeOrbit), Orbital("Duna"), Orbital("Ike", IkeOrbit)), drop));
            // No moon: never.
            Assert.False(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Orbital("Duna")), loose));
            Assert.False(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Rotation("Duna", IkeOrbit), Orbital("Duna")), drop));
            // Station + moon stays the extractor's fail-closed shape.
            Assert.False(ArrivalHoldPlanner.UsesMoonConfigurationHold(
                Set(Orbital("Duna"), Orbital("Ike", IkeOrbit), Station("Duna")), loose));
        }

        // === Engage ==========================================================================

        [Fact]
        public void IkeOrbitOnly_EngagesMoonHold_OnIkeOrbitalPeriod()
        {
            var r = Compute(IkeOrbitTrip());
            Assert.True(r.Applied, "a lone-moon arrival must take the moon hold");
            Assert.True(r.IsConfigHold);
            Assert.Equal(1, r.ConfigMoonCount);
            Assert.False(r.IsStationHold);
            Assert.False(r.IsJointHold);
            Assert.Null(r.AmberReason);
            Assert.Equal(IkeOrbit, r.AlignPeriodSeconds, 3);
            Assert.True(r.HoldSeconds > 0.0 && r.HoldSeconds <= IkeOrbit + 1e-9,
                $"hold must lie in (0, P_Ike], got {r.HoldSeconds}");
            Assert.Equal(1.0e6, r.HoldAtUT, 9);
        }

        [Fact]
        public void IkeOrbitOnly_PerLoopHold_PutsIkeBackAtEveryEncounter()
        {
            // Every replayed loop's total live shift is a whole number of Ike orbits, within
            // Ike's SOI-crossing time: the moon is where the recording had it.
            var r = Compute(IkeOrbitTrip());
            Assert.True(r.Applied);
            double entryOffset0 = 350.0; // phaseAnchorUT - spanStartUT, no loiter cuts
            for (long n = 0; n <= 12; n++)
            {
                double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(
                    r.HoldSeconds, n, KerbinDunaSynodic, r.AlignPeriodSeconds);
                Assert.InRange(wN, 0.0, r.AlignPeriodSeconds + 1e-6);
                double shift = entryOffset0 + n * KerbinDunaSynodic + wN;
                double err = MissionPeriodicity.CircularPhaseError(shift, IkeOrbit);
                Assert.True(err <= IkeSoiTolerance,
                    $"loop {n}: Ike phase error {err:F1}s exceeds SOI tolerance {IkeSoiTolerance:F1}s");
            }
        }

        [Fact]
        public void IkeLanding_TidallyLocked_EngagesMoonHold()
        {
            var trip = IkeOrbitTrip();
            trip.Add(Rotation("Ike", IkeOrbit)); // tidally locked: rotation == orbit
            var r = Compute(trip);
            Assert.True(r.Applied);
            Assert.True(r.IsConfigHold);
            Assert.Equal(IkeOrbit, r.AlignPeriodSeconds, 3);
            Assert.Null(r.AmberReason);
        }

        [Fact]
        public void DunaLandingWithAlignmentOff_StillAlignsIke()
        {
            // Off drops the planet's rotation; the moon's orbital phase is never dropped.
            var trip = IkeOrbitTrip();
            trip.Add(Rotation("Duna", IkeOrbit));
            var r = Compute(trip, mode: TransitedBodyRotationMode.Drop);
            Assert.True(r.Applied);
            Assert.True(r.IsConfigHold);
            Assert.Equal(IkeOrbit, r.AlignPeriodSeconds, 3);
        }

        [Fact]
        public void DunaLandingWithAlignmentOn_KeepsShippedRotationHold()
        {
            var trip = IkeOrbitTrip();
            trip.Add(Rotation("Duna", IkeOrbit));
            var r = Compute(trip);
            Assert.True(r.Applied);
            Assert.False(r.IsConfigHold);
            Assert.Equal(IkeOrbit, r.AlignPeriodSeconds, 3); // T_rot(Duna), the shipped model
        }

        // === Honest declines ==================================================================

        [Fact]
        public void NonLockedMoonLanding_DeclinesWithAmber_NeverSilent()
        {
            // A landing on a moon whose spin is incommensurate with its orbit: the scan within
            // the slack budget cannot line up both, so the trip stays as recorded WITH a reason.
            var trip = new List<PhaseConstraint>
            {
                Orbital("Duna"),
                Orbital("Slowmoon", 388587.8),
                Rotation("Slowmoon", 28255.0),
            };
            var r = Compute(trip, mode: TransitedBodyRotationMode.Tight, loopSlackSeconds: 1.0e6);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("1 constrained moon of 'Duna'", r.AmberReason);
        }

        [Fact]
        public void LoneMoon_SlackBelowOneMoonOrbit_DeclinesWithAmber()
        {
            var r = Compute(IkeOrbitTrip(), loopSlackSeconds: 30000.0);
            Assert.False(r.Applied);
            Assert.NotNull(r.AmberReason);
            Assert.Contains("loop slack", r.AmberReason);
        }

        [Fact]
        public void LoneMoon_PlannerStillRefusesAPostEntryCut()
        {
            // The planner's rigidity guard is unchanged; the BUILDER removes the destination cut
            // before calling it (KeepLaunchSideCutsForMoonConfigurationHold below).
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1.1e6, LengthSeconds = 200.0 },
            };
            var r = Compute(IkeOrbitTrip(), cuts: cuts);
            Assert.False(r.Applied);
            Assert.Contains("loiter cut", r.AmberReason);
        }

        // === Builder: the destination loiter replays whole for a moon hold ====================

        private static ConstraintExtraction Extraction(List<PhaseConstraint> cs)
            => new ConstraintExtraction { Constraints = cs, LaunchBodyName = "Kerbin" };

        private static ReaimMissionPlan Plan()
            => new ReaimMissionPlan { Supported = true, LaunchBody = "Kerbin", TargetBody = "Duna",
                CommonAncestor = "Sun", RecordedArrivalUT = 1.0e6 };

        private static readonly GhostPlaybackLogic.LoopCut LaunchCut =
            new GhostPlaybackLogic.LoopCut { StartUT = 1.0e5, LengthSeconds = 5.0e4 };
        private static readonly GhostPlaybackLogic.LoopCut DestCut =
            new GhostPlaybackLogic.LoopCut { StartUT = 1.1e6, LengthSeconds = 6000.0 };

        private static IReadOnlyList<GhostPlaybackLogic.LoopCut> KeepCuts(
            List<PhaseConstraint> trip, IReadOnlyList<GhostPlaybackLogic.LoopCut> all,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> launchSide, out int postEntryCuts)
        {
            int moons;
            return MissionLoopUnitBuilder.KeepLaunchSideCutsForMoonConfigurationHold(
                Extraction(trip), Plan(), all, launchSide, TransitedBodyRotationMode.Loose,
                new DunaFake(), out moons, out postEntryCuts);
        }

        // The span and cadence the builder hands the wrapper: slack = cadence - (span - cuts).
        private static ArrivalHoldPlanner.ArrivalHoldResult BuilderHold(
            List<PhaseConstraint> trip, IReadOnlyList<GhostPlaybackLogic.LoopCut> all,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> launchSide, double cadence, double span,
            out IReadOnlyList<GhostPlaybackLogic.LoopCut> cutsUsed)
        {
            return MissionLoopUnitBuilder.ComputeArrivalHoldKeepingMoonLoiterWhole(
                "Ike Trip", Extraction(trip), Plan(), all, launchSide,
                TransitedBodyRotationMode.Loose, 350.0, 0.0, new DunaFake(),
                KerbinDunaSynodic, cadence, span, out cutsUsed);
        }

        [Fact]
        public void Builder_LoneMoon_DropsDestinationCut_KeepsLaunchSide()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut, DestCut };
            var launchSide = new List<GhostPlaybackLogic.LoopCut> { LaunchCut };
            var kept = KeepCuts(IkeOrbitTrip(), all, launchSide, out int postEntry);
            Assert.Same(launchSide, kept);
            Assert.Equal(1, postEntry);
        }

        [Fact]
        public void Builder_LoneMoon_HoldEngages_KeepsLoiterWhole_AndLogs()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut, DestCut };
            var launchSide = new List<GhostPlaybackLogic.LoopCut> { LaunchCut };
            // Plenty of slack: cadence one synodic period, span a tenth of it.
            var r = BuilderHold(IkeOrbitTrip(), all, launchSide,
                KerbinDunaSynodic, 0.1 * KerbinDunaSynodic, out var used);
            Assert.True(r.Applied);
            Assert.True(r.IsConfigHold);
            Assert.Same(launchSide, used);
            Assert.Contains(logLines, l => l.Contains("[Reaim]")
                && l.Contains("MOON HOLD keeps the destination loiter whole")
                && l.Contains("dest=Duna") && l.Contains("moons=1")
                && l.Contains("droppedPostEntryCuts=1") && l.Contains("keptLaunchSideCuts=1"));
        }

        [Fact]
        public void Builder_LoneMoon_HoldDeclines_RestoresCompressedLoiter()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut, DestCut };
            var launchSide = new List<GhostPlaybackLogic.LoopCut> { LaunchCut };
            // Span ends just under the cadence: with the destination cut dropped the slack is
            // cadence - (span - launchCut) = 1000 + 5e4 s, below one Ike orbit, so the hold declines.
            double cadence = KerbinDunaSynodic;
            double span = cadence - 1000.0;
            var r = BuilderHold(IkeOrbitTrip(), all, launchSide, cadence, span, out var used);
            Assert.False(r.Applied);
            Assert.Contains("loop slack", r.AmberReason);
            Assert.Same(all, used);
            Assert.DoesNotContain(logLines, l => l.Contains("MOON HOLD keeps"));
            Assert.Contains(logLines, l => l.Contains("[Reaim]")
                && l.Contains("MOON HOLD declined, destination loiter stays compressed")
                && l.Contains("restoredCuts=2") && l.Contains("loop slack"));
        }

        [Fact]
        public void Builder_LoneMoon_OnlyDestinationCut_ReturnsNull()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { DestCut };
            var kept = KeepCuts(IkeOrbitTrip(), all, new List<GhostPlaybackLogic.LoopCut>(), out int postEntry);
            Assert.Null(kept);
            Assert.Equal(1, postEntry);
        }

        [Fact]
        public void Builder_DunaLandingRotationHold_LeavesCutsUnchanged()
        {
            var trip = IkeOrbitTrip();
            trip.Add(Rotation("Duna", IkeOrbit));
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut, DestCut };
            var kept = KeepCuts(trip, all, new List<GhostPlaybackLogic.LoopCut> { LaunchCut }, out int postEntry);
            Assert.Same(all, kept);
            Assert.Equal(0, postEntry);
            var r = BuilderHold(trip, all, new List<GhostPlaybackLogic.LoopCut> { LaunchCut },
                KerbinDunaSynodic, 0.1 * KerbinDunaSynodic, out var used);
            Assert.Same(all, used);
            Assert.DoesNotContain(logLines, l => l.Contains("MOON HOLD"));
        }

        [Fact]
        public void Builder_NoPostEntryCut_LeavesCutsUnchanged()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut };
            var kept = KeepCuts(IkeOrbitTrip(), all, all, out int postEntry);
            Assert.Same(all, kept);
            Assert.Equal(0, postEntry);
        }

        [Fact]
        public void Builder_NoMoon_LeavesCutsUnchanged()
        {
            var all = new List<GhostPlaybackLogic.LoopCut> { LaunchCut, DestCut };
            var kept = KeepCuts(new List<PhaseConstraint> { Orbital("Duna") }, all,
                new List<GhostPlaybackLogic.LoopCut> { LaunchCut }, out int postEntry);
            Assert.Same(all, kept);
            Assert.Equal(0, postEntry);
        }
    }
}
