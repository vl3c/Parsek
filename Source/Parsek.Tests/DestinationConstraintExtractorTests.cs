using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Re-aim Phase 4, implementation Phase 2: the PURE destination-constraint selector
    // (DestinationConstraintExtractor.ExtractDestinationConstraints). It picks the arrival-solve
    // constraint set out of the mission's already-extracted constraints WITHOUT touching the Support
    // classification (the regression-critical invariant: cross-parent missions must stay
    // UnsupportedCrossParent so the re-aim path still runs). No engine wiring is exercised here.
    [Collection("Sequential")]
    public class DestinationConstraintExtractorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DestinationConstraintExtractorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionPeriodicity.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = false;
        }

        // === fixtures =========================================================================

        private static PhaseConstraint Rotation(string body, double period = 65517.0)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period = 1.0e7, bool crossParent = true)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = crossParent };

        // Resolves the parent (reference body) per a supplied map; everything else is the period/SOI
        // data the selector does not consult.
        private sealed class ParentFake : IBodyInfo
        {
            private readonly Dictionary<string, string> parents;
            public ParentFake(Dictionary<string, string> parents) { this.parents = parents; }
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => parents.TryGetValue(b, out string p) ? p : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => double.NaN;
        }

        private static IBodyInfo Bodies(params (string body, string parent)[] pairs)
            => new ParentFake(pairs.ToDictionary(p => p.body, p => p.parent));

        // === Phase-2 test cases ===============================================================

        [Fact]
        public void CrossParentLanding_Duna_EmitsDestRotationOnly()
        {
            // A captured-then-deorbit Duna landing: the extraction emits the launch-pad rotation, the
            // Duna landing rotation, and the Duna SOI-entry orbital. The selector keeps ONLY the Duna
            // rotation (DestRotation); it drops the launch-pad rotation and the target's own orbital.
            var all = new List<PhaseConstraint>
            {
                Rotation("Kerbin"),          // launch pad - not an arrival constraint
                Rotation("Duna"),            // the landing -> DestRotation
                Orbital("Duna"),             // the destination SOI entry - excluded (direction irrelevant)
            };
            var bodies = Bodies(("Duna", "Sun"), ("Kerbin", "Sun"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.True(r.HasLandingRotation);
            Assert.Equal(0, r.ConstrainedMoonCount);
            Assert.Single(r.Constraints);
            Assert.Equal(ConstraintKind.Rotation, r.Constraints[0].Kind);
            Assert.Equal("Duna", r.Constraints[0].BodyName);
        }

        [Fact]
        public void CrossParentLanding_WithOneMoon_EmitsDestRotationPlusMoonConfig()
        {
            // The recorded arc also enters Ike's SOI: DestRotation(Duna) + MoonConfig(Ike); the target's
            // own Duna orbital is still excluded.
            var all = new List<PhaseConstraint>
            {
                Rotation("Duna"),
                Orbital("Duna"),
                Orbital("Ike", period: 65518.0),
            };
            var bodies = Bodies(("Duna", "Sun"), ("Ike", "Duna"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.True(r.HasLandingRotation);
            Assert.Equal(1, r.ConstrainedMoonCount);
            Assert.Equal(2, r.Constraints.Count);
            Assert.Contains(r.Constraints, c => c.Kind == ConstraintKind.Rotation && c.BodyName == "Duna");
            Assert.Contains(r.Constraints, c => c.Kind == ConstraintKind.Orbital && c.BodyName == "Ike");
            Assert.DoesNotContain(r.Constraints, c => c.BodyName == "Duna" && c.Kind == ConstraintKind.Orbital);
        }

        [Fact]
        public void OrbitOnlyFlyby_NoLanding_EmptyConstraintsButSupported()
        {
            // A captured orbit with no landing records no target rotation: the selector emits nothing
            // (any window is faithful for an orbit-only arrival), still Supported.
            var all = new List<PhaseConstraint>
            {
                Rotation("Kerbin"),
                Orbital("Duna"),
            };
            var bodies = Bodies(("Duna", "Sun"), ("Kerbin", "Sun"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.False(r.HasLandingRotation);
            Assert.Equal(0, r.ConstrainedMoonCount);
            Assert.Empty(r.Constraints);
        }

        [Fact]
        public void ZeroMoonDestination_Moho_DestRotationOnly()
        {
            // A 0-moon destination (Moho): DestRotation only.
            var all = new List<PhaseConstraint>
            {
                Rotation("Moho"),
                Orbital("Moho"),
            };
            var bodies = Bodies(("Moho", "Sun"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Moho", bodies);

            Assert.True(r.Supported);
            Assert.Equal(0, r.ConstrainedMoonCount);
            Assert.Single(r.Constraints);
            Assert.Equal("Moho", r.Constraints[0].BodyName);
            Assert.Equal(ConstraintKind.Rotation, r.Constraints[0].Kind);
        }

        [Fact]
        public void TwoConstrainedMoons_JoolClass_FailsClosed()
        {
            // A Jool-class destination the recorded arc visits two moons of: detect and fail closed to
            // faithful (no constraints), Supported == false with a reason.
            var all = new List<PhaseConstraint>
            {
                Rotation("Jool"),
                Orbital("Jool"),
                Orbital("Laythe"),
                Orbital("Vall"),
            };
            var bodies = Bodies(("Jool", "Sun"), ("Laythe", "Jool"), ("Vall", "Jool"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Jool", bodies);

            Assert.False(r.Supported);
            Assert.Equal(2, r.ConstrainedMoonCount);
            Assert.Empty(r.Constraints);             // fail closed
            Assert.Contains("2 constrained moons", r.Reason);
        }

        [Fact]
        public void ExcludesTargetOwnOrbitalAndForeignMoons()
        {
            // The target's own orbital is excluded; so is a moon of a DIFFERENT body (e.g. the launch
            // body's own moon), which is not a constrained moon of the target.
            var all = new List<PhaseConstraint>
            {
                Rotation("Duna"),
                Orbital("Duna"),             // target's own SOI entry - excluded
                Orbital("Mun"),              // a moon of KERBIN, not Duna - not a target moon
            };
            var bodies = Bodies(("Duna", "Sun"), ("Mun", "Kerbin"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.Equal(0, r.ConstrainedMoonCount);  // Mun does not count (parent is Kerbin, not Duna)
            Assert.Single(r.Constraints);
            Assert.Equal("Duna", r.Constraints[0].BodyName);
            Assert.Equal(ConstraintKind.Rotation, r.Constraints[0].Kind);
        }

        [Fact]
        public void DuplicateMoonSoiEntries_CountedOnce()
        {
            // The arc enters Ike's SOI twice (two Orbital(Ike) constraints): one MoonConfig, count 1.
            var all = new List<PhaseConstraint>
            {
                Rotation("Duna"),
                Orbital("Ike", period: 65518.0),
                Orbital("Ike", period: 65518.0),
            };
            var bodies = Bodies(("Duna", "Sun"), ("Ike", "Duna"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.Equal(1, r.ConstrainedMoonCount);
            Assert.Equal(2, r.Constraints.Count);     // DestRotation(Duna) + one MoonConfig(Ike)
        }

        [Fact]
        public void MoonRotation_NotCountedAsMoonConfig()
        {
            // A moon's ROTATION constraint must never be mistaken for a MoonConfig (which is an Orbital
            // SOI entry). With Rotation(Ike) present, only the Orbital(Ike) counts as the moon; Ike's
            // rotation is dropped, and DestRotation stays the target (Duna) rotation.
            var all = new List<PhaseConstraint>
            {
                Rotation("Duna"),
                Rotation("Ike"),                       // a moon's rotation - must be ignored entirely
                Orbital("Ike", period: 65518.0),       // the only thing that makes Ike a constrained moon
            };
            var bodies = Bodies(("Duna", "Sun"), ("Ike", "Duna"));

            var r = DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.True(r.Supported);
            Assert.Equal(1, r.ConstrainedMoonCount);           // the Orbital(Ike), not the Rotation(Ike)
            Assert.Equal(2, r.Constraints.Count);              // DestRotation(Duna) + MoonConfig(Ike)
            Assert.Equal("Duna", r.Constraints[0].BodyName);   // DestRotation is the target's rotation
            Assert.Equal(ConstraintKind.Rotation, r.Constraints[0].Kind);
            Assert.DoesNotContain(r.Constraints, c => c.Kind == ConstraintKind.Rotation && c.BodyName == "Ike");
        }

        [Fact]
        public void DoesNotMutateInput()
        {
            // Pure selector: the caller's constraint list is untouched (it is the live extraction output).
            var all = new List<PhaseConstraint>
            {
                Rotation("Kerbin"), Rotation("Duna"), Orbital("Duna"), Orbital("Ike", period: 65518.0),
            };
            var bodies = Bodies(("Duna", "Sun"), ("Ike", "Duna"), ("Kerbin", "Sun"));
            int before = all.Count;

            DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            Assert.Equal(before, all.Count);
            Assert.Equal(ConstraintKind.Rotation, all[0].Kind);
            Assert.Equal("Kerbin", all[0].BodyName);  // first element unchanged
        }

        [Fact]
        public void SummaryLogLine_EmittedOnce()
        {
            var all = new List<PhaseConstraint> { Rotation("Duna"), Orbital("Duna") };
            var bodies = Bodies(("Duna", "Sun"));

            DestinationConstraintExtractor.ExtractDestinationConstraints(all, "Duna", bodies);

            var lines = logLines.Where(l => l.Contains("[ReaimArrival]") && l.Contains("dest-constraints")).ToList();
            Assert.Single(lines);
            Assert.Contains("target=Duna", lines[0]);
            Assert.Contains("landingRotation=", lines[0]);
            Assert.Contains("moons=", lines[0]);
            Assert.Contains("supported=", lines[0]);
        }
    }
}
