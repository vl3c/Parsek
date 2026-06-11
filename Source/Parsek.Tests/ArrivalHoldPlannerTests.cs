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
        private static PhaseConstraint Rotation(string body)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = 65000.0, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

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
        public void TwoConstrainedMoons_Unsupported_ReturnsNone()
        {
            var jool = new List<PhaseConstraint>
            {
                Rotation("Jool"), Orbital("Jool"), Orbital("Laythe"), Orbital("Vall"),
            };
            var r = ArrivalHoldPlanner.ComputeArrivalHold(
                jool, "Jool", 1000.0, TransitedBodyRotationMode.Loose, 350.0, 0.0, null, new HoldFake());
            Assert.False(r.Applied);
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
    }
}
