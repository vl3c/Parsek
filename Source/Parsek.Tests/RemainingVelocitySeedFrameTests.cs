using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// REMAINING-RECORDED-VELOCITY-SEEDS: the seeds left after TERMINAL-SPAWN-TAIL-ORBIT-SURFACE-VELOCITY
    /// and MAP-TAIL-SEED-RECORDED-VELOCITY-FRAME that paired a recorded <c>TrajectoryPoint.velocity</c>
    /// (a world vector, surface-relative below the inverse-rotation altitude, in the recording
    /// session's world yaw) with a position rebuilt in this session as if it were inertial and in
    /// this session's axes:
    /// <list type="bullet">
    ///   <item>state-vector map ghosts (create / update / overlap) and the spawner's endpoint
    ///     state-vector repair and no-override SpawnAtPosition callers, through the pure core
    ///     <see cref="OrbitReseed.ResolveRecordedSampleSeedVelocity"/> (pair resolved, else the
    ///     recorded vector as before);</item>
    ///   <item>the scene-exit finalizer's predicted-tail reseed and recovery start (same session),
    ///     through <see cref="OrbitReseed.ResolveSameSessionRecordedVelocity"/> (pair resolved,
    ///     else the stock altitude rule).</item>
    /// </list>
    /// Truth is constructed in body-fixed axes: an inertial velocity and the surface-relative one
    /// it implies (minus stock <c>getRFrmVel</c>), recorded in axes yawed by the record session and
    /// rebuilt at another yaw. Orbits come from Parsek's pure two-body solver.
    /// </summary>
    [Collection("Sequential")]
    public class RemainingVelocitySeedFrameTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRotationPeriod = 21549.425;
        private const double KerbinInverseRotThreshold = 100000.0;

        private readonly List<string> logLines = new List<string>();

        public RemainingVelocitySeedFrameTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---------- pair resolution at either sample ----------

        [Fact]
        public void PairVelocityAtTheTail_IsTheResolversTailVelocity()
        {
            Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                recordYawDeg: 70.0, spawnYawDeg: -20.0);
            Assert.True(RecordedTailVelocityResolver.TryResolve(
                s.PreviousPos, s.Previous.ut, Vel(s.Previous), s.TailPos, s.Tail.ut, Vel(s.Tail),
                KerbinRotationPeriod, preferRotatingOnTie: true, out RecordedTailVelocityResolution direct));

            Assert.True(OrbitReseed.TryResolveRecordedPairVelocityAtSample(
                s.Previous, s.Tail, sampleIsTail: true, s.PreviousPos, s.TailPos, s.TailPos,
                KerbinRotationPeriod, true, KerbinInverseRotThreshold,
                out RecordedTailVelocityResolution r, out Vector3d v));
            Assert.InRange((v - direct.InertialVelocityYup).magnitude, 0.0, 1e-6);
            Assert.True(r.SampleWasRotatingFrame);
        }

        [Theory]
        [InlineData(true, 0.0, 0.0)]
        [InlineData(true, 123.0, -40.0)]
        [InlineData(false, 0.0, 0.0)]
        [InlineData(false, -64.0, 150.0)]
        public void PairVelocityAtThePreviousSample_RecoversItsInertialVelocity(
            bool rotating, double recordYawDeg, double spawnYawDeg)
        {
            // A 30 s pair: an inertial reading carries the previous sample's vector by the spin
            // over the whole gap (0.5 deg, ~19 m/s here), a rotating one adds the frame
            // velocity at the previous sample.
            Synthetic s = Build(rotating, alt: rotating ? 95000.0 : 101591.0, speed: 2241.4,
                flightPathDeg: 0.6, recordYawDeg: recordYawDeg, spawnYawDeg: spawnYawDeg, dt: 30.0);

            Assert.True(OrbitReseed.TryResolveRecordedPairVelocityAtSample(
                s.Previous, s.Tail, sampleIsTail: false, s.PreviousPos, s.TailPos, s.PreviousPos,
                KerbinRotationPeriod, true, KerbinInverseRotThreshold,
                out RecordedTailVelocityResolution r, out Vector3d v), r.DeclineReason);
            Assert.Equal(rotating, r.SampleWasRotatingFrame);
            Assert.InRange((v - s.PreviousTruthInertial).magnitude, 0.0, 0.5);
        }

        // ---------- map state-vector ghosts and spawner seeds (cross-session core) ----------

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.0, 90.0)]
        [InlineData(140.0, -35.0)]
        public void Gs7LikeAscent_SurfaceRelativeLowSample_SeedsTheInertialOrbit(
            double recordYawDeg, double spawnYawDeg)
        {
            // GS-7 run 2026-10-12_1856: alt=26518.7 |vel|=799.46 on an ascent (surface-relative).
            Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                recordYawDeg: recordYawDeg, spawnYawDeg: spawnYawDeg);

            OrbitReseed.RecordedSampleSeedVelocity v = SeedAt(s, s.Tail, s.TailPos);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 0.5);

            TwoBodyOrbit truth = Solve(s.TailPos, s.TailTruthInertial);
            TwoBodyOrbit seeded = Solve(s.TailPos, v.VelocityYup);
            Assert.InRange(Math.Abs(seeded.SemiMajorAxis - truth.SemiMajorAxis), 0.0, 200.0);
            Assert.InRange(Math.Abs(seeded.Eccentricity - truth.Eccentricity), 0.0, 1e-3);
            Assert.InRange(Math.Abs(seeded.Inclination - truth.Inclination) * 180.0 / Math.PI, 0.0, 0.1);

            // The raw vector (what these sites seeded before) misses the frame velocity.
            TwoBodyOrbit raw = Solve(s.TailPos, Vel(s.Tail));
            Assert.True(Math.Abs(raw.SemiMajorAxis - truth.SemiMajorAxis) > 5000.0,
                $"raw sma {raw.SemiMajorAxis:F0} vs truth {truth.SemiMajorAxis:F0}");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-120.0)]
        public void Lab7RecordedPair_StateVectorSeedIsTheStockOrbit(double spawnYawDeg)
        {
            // LAB-7's recorded tail (93 km, surface-relative) as a state-vector map sample.
            TrajectoryPoint previous = TerminalSpawnTailOrbitRotatingFrameTests.Lab7TailPrevious;
            TrajectoryPoint tail = TerminalSpawnTailOrbitRotatingFrameTests.Lab7Tail;
            var points = new List<TrajectoryPoint> { previous, tail };
            Func<double, double, double, Vector3d> lookup = Lookup(spawnYawDeg);
            Vector3d tailPos = lookup(tail.latitude, tail.longitude, tail.altitude);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveRecordedSampleSeedVelocity(
                points, Sections(points, ReferenceFrame.Absolute), tail, tailPos, lookup,
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            TwoBodyOrbit orbit = Solve(tailPos, v.VelocityYup);
            Assert.InRange(orbit.SemiMajorAxis,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockSma - 200.0,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockSma + 200.0);
            Assert.InRange(orbit.Eccentricity, 0.0094, 0.0114);
        }

        [Fact]
        public void Lab10LikeInertialHighSample_SameSession_IsUnchanged()
        {
            // LAB-10 run 2026-10-18_0104: `state vectors alt=101591 spd=2241.4 ... sma=700213`,
            // the inertial, same-session case the old seed already got right.
            Synthetic s = Build(rotating: false, alt: 101591.0, speed: 2241.4, flightPathDeg: 0.3,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = SeedAt(s, s.Tail, s.TailPos);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.False(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - Vel(s.Tail)).magnitude, 0.0, 0.05);
            Assert.Equal(Vel(s.Tail).magnitude, v.VelocityYup.magnitude, 6);
        }

        [Fact]
        public void CrossSessionSingleSample_Declines_AndKeepsTheRecordedVector()
        {
            Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                recordYawDeg: 77.0, spawnYawDeg: 0.0);
            var single = new List<TrajectoryPoint> { s.Tail };

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveRecordedSampleSeedVelocity(
                single, Sections(single, ReferenceFrame.Absolute), s.Tail, s.TailPos, Lookup(0.0),
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourceRecorded, v.Source);
            Assert.Equal(GhostMapPresence.GapGlidePairSingleSample, v.DeclineReason);
            Assert.Equal(0.0, (Vel(s.Tail) - v.VelocityYup).magnitude);
        }

        [Fact]
        public void RelativeSectionOrInterpolatedSample_Declines_AndKeepsTheRecordedVector()
        {
            Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);
            var points = new List<TrajectoryPoint> { s.Previous, s.Tail };

            OrbitReseed.RecordedSampleSeedVelocity relative = OrbitReseed.ResolveRecordedSampleSeedVelocity(
                points, Sections(points, ReferenceFrame.Relative), s.Tail, s.TailPos, Lookup(0.0),
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.DeclinePairRelativeSection, relative.DeclineReason);
            Assert.Equal(0.0, (Vel(s.Tail) - relative.VelocityYup).magnitude);

            TrajectoryPoint lerped = s.Tail;
            lerped.ut -= 0.25;
            OrbitReseed.RecordedSampleSeedVelocity interpolated = OrbitReseed.ResolveRecordedSampleSeedVelocity(
                points, Sections(points, ReferenceFrame.Absolute), lerped, s.TailPos, Lookup(0.0),
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(GhostMapPresence.GapGlidePairPointNotFound, interpolated.DeclineReason);
            Assert.Equal(0.0, (Vel(lerped) - interpolated.VelocityYup).magnitude);
        }

        [Fact]
        public void SectionStartSample_PairsForward_AndSeedsItsOwnVelocity()
        {
            // The sample opens its section, so it pairs with the one after it and is the
            // pair's PREVIOUS sample: its own vector is carried, not the later one's.
            Synthetic s = Build(rotating: true, alt: 60000.0, speed: 1500.0, flightPathDeg: 10.0,
                recordYawDeg: -30.0, spawnYawDeg: 45.0, dt: 2.0);
            var points = new List<TrajectoryPoint> { s.Previous, s.Tail };
            var sections = new List<TrackSection>
            {
                new TrackSection { referenceFrame = ReferenceFrame.Absolute, startUT = s.Previous.ut - 10.0, endUT = s.Previous.ut },
                new TrackSection { referenceFrame = ReferenceFrame.Absolute, startUT = s.Previous.ut, endUT = s.Tail.ut + 1.0 }
            };

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveRecordedSampleSeedVelocity(
                points, sections, s.Previous, s.PreviousPos, Lookup(45.0),
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.Equal(s.Previous.ut, v.PairPreviousUT);
            Assert.InRange((v.VelocityYup - s.PreviousTruthInertial).magnitude, 0.0, 0.5);
        }

        [Fact]
        public void SpawnPositionAwayFromTheSample_AddsTheFrameVelocityThere()
        {
            Synthetic s = Build(rotating: true, alt: 85000.0, speed: 2100.0, flightPathDeg: 0.0,
                recordYawDeg: 15.0, spawnYawDeg: 15.0);
            Vector3d seedPos = RecordedTailVelocityResolver.RotateAboutY(s.TailPos, 0.2);

            OrbitReseed.RecordedSampleSeedVelocity atTail = SeedAt(s, s.Tail, s.TailPos);
            OrbitReseed.RecordedSampleSeedVelocity atSeed = SeedAt(s, s.Tail, seedPos);
            Vector3d expectedShift =
                OrbitReseed.ComputeRotatingFrameVelocityYup(seedPos, KerbinRotationPeriod)
                - OrbitReseed.ComputeRotatingFrameVelocityYup(s.TailPos, KerbinRotationPeriod);
            Assert.InRange(((atSeed.VelocityYup - atTail.VelocityYup) - expectedShift).magnitude, 0.0, 1e-6);
        }

        [Theory]
        [InlineData(TerminalState.Orbiting, true)]
        [InlineData(TerminalState.Landed, false)]
        [InlineData(TerminalState.Splashed, false)]
        [InlineData(TerminalState.SubOrbital, false)]
        public void NoOverrideSpawn_ResolvesOnlyAnAirborneSpawnableTerminal(TerminalState terminal, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.ShouldResolveRecordedSpawnVelocity(terminal));
            Assert.False(VesselSpawner.ShouldResolveRecordedSpawnVelocity(null));
        }

        // ---------- scene-exit finalizer (same session) ----------

        [Fact]
        public void Eva4LikeParachuteDescent_SingleSample_AltitudeRuleAddsTheFrameVelocity()
        {
            // EVA-4 run 2026-10-11_1458: alt=1666.3 |vel|=14.88, a parachute descent.
            Synthetic s = Build(rotating: true, alt: 1666.3, speed: 14.88, flightPathDeg: -88.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Tail, Vector3d.zero, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourceAltitudeRule, v.Source);
            Assert.True(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 1e-3);
            // The recorded vector alone misses ~175 m/s of eastward motion near the equator.
            Assert.InRange((Vel(s.Tail) - s.TailTruthInertial).magnitude, 150.0, 180.0);
        }

        [Fact]
        public void Eva4LikeParachuteDescent_Pair_ResolvesRotating()
        {
            // Near the equator a vertical descent still has a spin-normal (XZ) component, so
            // the pair fits it: surface-relative, the frame velocity added.
            Synthetic s = Build(rotating: true, alt: 1666.3, speed: 14.88, flightPathDeg: -88.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                s.Previous, s.Tail, s.PreviousPos, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 0.05);
        }

        [Fact]
        public void UnresolvedPair_FallsBackToTheAltitudeRule()
        {
            // A 40 s pair is outside the resolver's gap window: the pair declines and the
            // altitude rule answers.
            Synthetic s = Build(rotating: true, alt: 1666.3, speed: 14.88, flightPathDeg: -88.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0, dt: 40.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                s.Previous, s.Tail, s.PreviousPos, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourceAltitudeRule, v.Source);
            Assert.Equal(RecordedTailVelocityResolver.DeclinePairGap, v.DeclineReason);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 1e-3);
        }

        [Fact]
        public void Gs7LikeAscent_SameSessionPair_ResolvesRotating()
        {
            Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                s.Previous, s.Tail, s.PreviousPos, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 0.5);

            TwoBodyOrbit truth = Solve(s.TailPos, s.TailTruthInertial);
            TwoBodyOrbit seeded = Solve(s.TailPos, v.VelocityYup);
            Assert.InRange(Math.Abs(seeded.SemiMajorAxis - truth.SemiMajorAxis), 0.0, 200.0);
        }

        [Fact]
        public void InertialHighSample_SameSession_IsUnchanged()
        {
            Synthetic s = Build(rotating: false, alt: 101591.0, speed: 2241.4, flightPathDeg: 0.3,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity single = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Tail, Vector3d.zero, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(0.0, (Vel(s.Tail) - single.VelocityYup).magnitude);
            Assert.False(single.SampleWasRotatingFrame);

            OrbitReseed.RecordedSampleSeedVelocity pair = OrbitReseed.ResolveSameSessionRecordedVelocity(
                s.Previous, s.Tail, s.PreviousPos, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, pair.Source);
            Assert.InRange((pair.VelocityYup - Vel(s.Tail)).magnitude, 0.0, 0.05);
        }

        [Fact]
        public void BackgroundVesselRecordedInertialBelowTheThreshold_PairKeepsItInertial()
        {
            // Debris at 40 km recorded while the ACTIVE vessel was above 100 km: the world was
            // inertial, so its vector is inertial although the altitude rule says rotating. The
            // pair decides from the data and adds nothing.
            Synthetic s = Build(rotating: false, alt: 40000.0, speed: 1800.0, flightPathDeg: -5.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                s.Previous, s.Tail, s.PreviousPos, s.TailPos, KerbinRotationPeriod, true, KerbinInverseRotThreshold);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.AltitudeRuleRotating);
            Assert.False(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.TailTruthInertial).magnitude, 0.0, 0.5);
        }

        [Fact]
        public void NonRotatingBody_AltitudeRuleAddsNothing()
        {
            Synthetic s = Build(rotating: true, alt: 5000.0, speed: 300.0, flightPathDeg: 10.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);
            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Tail, Vector3d.zero, s.TailPos, KerbinRotationPeriod, false, KerbinInverseRotThreshold);
            Assert.Equal(0.0, (Vel(s.Tail) - v.VelocityYup).magnitude);
            Assert.False(v.SampleWasRotatingFrame);
        }

        // Review follow-up: a sample that opens its list has no predecessor, so it pairs
        // FORWARD with the one after it before the altitude rule answers.
        [Fact]
        public void BackgroundVesselInertialBelowTheThreshold_NoPredecessor_PairsForward()
        {
            Synthetic s = Build(rotating: false, alt: 40000.0, speed: 1800.0, flightPathDeg: -5.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Previous, Vector3d.zero, s.PreviousPos, KerbinRotationPeriod, true,
                KerbinInverseRotThreshold, s.Tail, s.TailPos);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.AltitudeRuleRotating);
            Assert.False(v.SampleWasRotatingFrame);
            Assert.Null(v.DeclineReason);
            Assert.InRange((v.VelocityYup - s.PreviousTruthInertial).magnitude, 0.0, 0.5);
        }

        [Fact]
        public void Eva4LikeDescent_NoPredecessor_PairsForwardAndResolvesRotating()
        {
            Synthetic s = Build(rotating: true, alt: 1666.3, speed: 14.88, flightPathDeg: -88.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Previous, Vector3d.zero, s.PreviousPos, KerbinRotationPeriod, true,
                KerbinInverseRotThreshold, s.Tail, s.TailPos);
            Assert.Equal(OrbitReseed.SeedVelocitySourcePair, v.Source);
            Assert.True(v.SampleWasRotatingFrame);
            Assert.InRange((v.VelocityYup - s.PreviousTruthInertial).magnitude, 0.0, 0.05);
        }

        [Fact]
        public void ForwardPairOutsideTheGapWindow_FallsBackToTheAltitudeRule()
        {
            Synthetic s = Build(rotating: true, alt: 1666.3, speed: 14.88, flightPathDeg: -88.0,
                recordYawDeg: 0.0, spawnYawDeg: 0.0, dt: 40.0);

            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveSameSessionRecordedVelocity(
                null, s.Previous, Vector3d.zero, s.PreviousPos, KerbinRotationPeriod, true,
                KerbinInverseRotThreshold, s.Tail, s.TailPos);
            Assert.Equal(OrbitReseed.SeedVelocitySourceAltitudeRule, v.Source);
            Assert.Equal("pair-single-sample", v.DeclineReason);
            Assert.InRange((v.VelocityYup - s.PreviousTruthInertial).magnitude, 0.0, 1e-3);
        }

        [Fact]
        public void RecordedPointSuccessor_StaysInsideOneListAndBody()
        {
            TrajectoryPoint a = P(10.0, "Kerbin"), b = P(11.0, "Kerbin"), c = P(12.0, "Mun"), d = P(13.0, "Mun");
            var rec = new Recording { RecordingId = "succ" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute, startUT = 10.0, endUT = 11.5,
                frames = new List<TrajectoryPoint> { a, b }
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative, startUT = 11.5, endUT = 13.5,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = new List<TrajectoryPoint> { c, d }
            });

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointSuccessor(rec, a, out TrajectoryPoint after));
            Assert.Equal(11.0, after.ut);
            // b closes its list: a successor across the section seam does not count.
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointSuccessor(rec, b, out _));
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointSuccessor(rec, c, out TrajectoryPoint cNext));
            Assert.Equal(13.0, cNext.ut);
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointSuccessor(rec, d, out _));
        }

        [Theory]
        [InlineData(false, true, true, true, TerminalState.Orbiting, true)]
        [InlineData(true, true, true, true, TerminalState.Orbiting, false)]
        [InlineData(false, false, true, true, TerminalState.Orbiting, false)]
        [InlineData(false, true, false, true, TerminalState.Orbiting, false)]
        [InlineData(false, true, true, false, TerminalState.Orbiting, false)]
        [InlineData(false, true, true, true, TerminalState.Landed, false)]
        [InlineData(false, true, true, true, TerminalState.Splashed, false)]
        public void NoOverrideSpawnVelocity_ResolvesOnlyAnAirborneSampleWithEverythingPresent(
            bool hasOrbitOverride, bool hasSample, bool hasBody, bool hasRecording,
            TerminalState terminal, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.ShouldResolveNoOverrideSpawnVelocity(
                hasOrbitOverride, hasSample, hasBody, hasRecording, terminal));
            Assert.False(VesselSpawner.ShouldResolveNoOverrideSpawnVelocity(false, true, true, true, null));
        }

        // ---------- finalizer predecessor lookup ----------

        [Fact]
        public void RecordedPointPredecessor_StaysInsideOneListAndBody()
        {
            TrajectoryPoint a = P(10.0, "Kerbin"), b = P(11.0, "Kerbin"), c = P(12.0, "Kerbin"), d = P(13.0, "Mun");
            var rec = new Recording { RecordingId = "pred" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute, startUT = 10.0, endUT = 11.5,
                frames = new List<TrajectoryPoint> { a, b }
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative, startUT = 11.5, endUT = 13.5,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = new List<TrajectoryPoint> { c, d }
            });

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(rec, b, out TrajectoryPoint before));
            Assert.Equal(10.0, before.ut);
            // c opens its list: a predecessor across the section seam does not count.
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(rec, c, out _));
            // d's predecessor is on another body.
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(rec, d, out _));
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(rec, a, out _));

            var flat = new Recording { RecordingId = "flat" };
            flat.Points.Add(a);
            flat.Points.Add(b);
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(flat, b, out TrajectoryPoint flatBefore));
            Assert.Equal(10.0, flatBefore.ut);
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryFindRecordedPointPredecessor(null, b, out _));
        }

        // ---------- logging ----------

        [Fact]
        public void HistoricalTailPairDecline_IsRateLimitedPerRecordingAndReason_AcceptedIsNot()
        {
            const string declined = "TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch declined: body=Kerbin x";
            const string accepted = "TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch: body=Kerbin x";
            for (int i = 0; i < 5; i++)
                OrbitReseed.LogHistoricalTailPairSeedLine(false, "rec-a", "Kerbin", "gap-pair-single-sample", declined);
            OrbitReseed.LogHistoricalTailPairSeedLine(false, "rec-a", "Kerbin", RecordedTailVelocityResolver.DeclineNoFit, declined);
            OrbitReseed.LogHistoricalTailPairSeedLine(false, "rec-b", "Kerbin", "gap-pair-single-sample", declined);
            for (int i = 0; i < 3; i++)
                OrbitReseed.LogHistoricalTailPairSeedLine(true, "rec-a", "Kerbin", null, accepted);

            Assert.Equal(3, logLines.FindAll(l => l.Contains("[OrbitReseed]") && l.Contains("WithEpoch declined: body=")).Count);
            Assert.Equal(3, logLines.FindAll(l => l.Contains("[OrbitReseed]") && l.Contains("WithEpoch: body=")).Count);
            Assert.Equal(
                "historical-tail-pair-declined-body:Kerbin-gap-pair-single-sample",
                OrbitReseed.HistoricalTailPairDeclineRateKey(null, "Kerbin", "gap-pair-single-sample"));
        }

        [Fact]
        public void SeedVelocityLine_IsInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Synthetic s = Build(rotating: true, alt: 26518.7, speed: 799.46, flightPathDeg: 35.0,
                    recordYawDeg: 0.0, spawnYawDeg: 0.0);
                OrbitReseed.RecordedSampleSeedVelocity v = SeedAt(s, s.Tail, s.TailPos);
                string line = OrbitReseed.FormatRecordedSampleSeedVelocityLine(
                    "State-vector seed velocity", "update", "rec-1", "Kerbin", s.Tail, v);
                Assert.StartsWith("State-vector seed velocity: site=update rec=rec-1 body=Kerbin ", line);
                Assert.Contains("alt=26518.7 source=pair-resolved decline=(none) velFrame=rotating altitudeRule=rotating", line);
                Assert.Contains("|recordedVel|=799.46 ", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------- helpers ----------

        private struct Synthetic
        {
            internal TrajectoryPoint Previous;
            internal TrajectoryPoint Tail;
            internal Vector3d PreviousPos;
            internal Vector3d TailPos;
            internal Vector3d TailTruthInertial;
            internal Vector3d PreviousTruthInertial;
            internal double SpawnYawDeg;
        }

        /// <summary>
        /// A recorded pair near the equator (lat 0.5, lon -74.5, heading east) whose tail has
        /// <paramref name="speed"/> in the recorded frame at <paramref name="flightPathDeg"/>.
        /// Rotating: the recorded vector is surface-relative and the motion is a straight
        /// body-fixed line. Inertial: the recorded vector is inertial and the motion a straight
        /// inertial line, so in body-fixed axes the earlier sample and its velocity sit the spin
        /// over <paramref name="dt"/> further round. Recorded vectors are expressed in axes yawed
        /// by -<paramref name="recordYawDeg"/>, positions rebuilt at <paramref name="spawnYawDeg"/>.
        /// </summary>
        private static Synthetic Build(
            bool rotating, double alt, double speed, double flightPathDeg,
            double recordYawDeg, double spawnYawDeg, double dt = 1.0)
        {
            Vector3d posBf = BodyFixed(0.5, -74.5, alt);
            Vector3d radial = posBf.normalized;
            Vector3d east = Vector3d.Cross(new Vector3d(0, 1, 0), radial).normalized * -1.0;
            double gamma = flightPathDeg * Math.PI / 180.0;
            Vector3d recordedFrameVelBf = east * (speed * Math.Cos(gamma)) + radial * (speed * Math.Sin(gamma));

            Vector3d spinTail = OrbitReseed.ComputeRotatingFrameVelocityYup(posBf, KerbinRotationPeriod);
            Vector3d inertialTailBf = rotating ? recordedFrameVelBf + spinTail : recordedFrameVelBf;
            double drift = RecordedTailVelocityResolver.InertialYawDriftRadians(KerbinRotationPeriod, dt);

            Vector3d previousBf;
            Vector3d inertialPreviousBf;
            if (rotating)
            {
                previousBf = posBf - recordedFrameVelBf * dt;
                inertialPreviousBf = recordedFrameVelBf
                    + OrbitReseed.ComputeRotatingFrameVelocityYup(previousBf, KerbinRotationPeriod);
            }
            else
            {
                previousBf = RecordedTailVelocityResolver.RotateAboutY(posBf - recordedFrameVelBf * dt, drift);
                inertialPreviousBf = RecordedTailVelocityResolver.RotateAboutY(recordedFrameVelBf, drift);
            }

            Vector3d recorded = RecordedTailVelocityResolver.RotateAboutY(
                recordedFrameVelBf, -recordYawDeg * Math.PI / 180.0);
            double spawnYaw = spawnYawDeg * Math.PI / 180.0;

            const double tailUT = 5000.0;
            return new Synthetic
            {
                Previous = FromBodyFixed(previousBf, tailUT - dt, recorded),
                Tail = FromBodyFixed(posBf, tailUT, recorded),
                PreviousPos = RecordedTailVelocityResolver.RotateAboutY(previousBf, spawnYaw),
                TailPos = RecordedTailVelocityResolver.RotateAboutY(posBf, spawnYaw),
                TailTruthInertial = RecordedTailVelocityResolver.RotateAboutY(inertialTailBf, spawnYaw),
                PreviousTruthInertial = RecordedTailVelocityResolver.RotateAboutY(inertialPreviousBf, spawnYaw),
                SpawnYawDeg = spawnYawDeg
            };
        }

        private static OrbitReseed.RecordedSampleSeedVelocity SeedAt(
            Synthetic s, TrajectoryPoint sample, Vector3d seedPos)
        {
            var points = new List<TrajectoryPoint> { s.Previous, s.Tail };
            return OrbitReseed.ResolveRecordedSampleSeedVelocity(
                points, Sections(points, ReferenceFrame.Absolute), sample, seedPos, Lookup(s.SpawnYawDeg),
                KerbinRotationPeriod, true, KerbinInverseRotThreshold);
        }

        private static List<TrackSection> Sections(List<TrajectoryPoint> points, ReferenceFrame frame)
        {
            return new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = frame,
                    startUT = points[0].ut - 1.0,
                    endUT = points[points.Count - 1].ut + 1.0
                }
            };
        }

        private static Func<double, double, double, Vector3d> Lookup(double spawnYawDeg)
        {
            double yaw = spawnYawDeg * Math.PI / 180.0;
            return (lat, lon, alt) => RecordedTailVelocityResolver.RotateAboutY(BodyFixed(lat, lon, alt), yaw);
        }

        private static Vector3d BodyFixed(double lat, double lon, double alt)
        {
            double r = KerbinRadius + alt;
            double la = lat * Math.PI / 180.0;
            double lo = lon * Math.PI / 180.0;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        private static TrajectoryPoint FromBodyFixed(Vector3d posBf, double ut, Vector3d recorded)
        {
            double r = posBf.magnitude;
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = Math.Asin(posBf.y / r) * 180.0 / Math.PI,
                longitude = Math.Atan2(posBf.z, posBf.x) * 180.0 / Math.PI,
                altitude = r - KerbinRadius,
                rotation = Quaternion.identity,
                velocity = new Vector3((float)recorded.x, (float)recorded.y, (float)recorded.z),
                bodyName = "Kerbin"
            };
        }

        private static TrajectoryPoint P(double ut, string body)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = ut * 0.01,
                longitude = ut * 0.02,
                altitude = 1000.0 + ut,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = body
            };
        }

        private static Vector3d Vel(TrajectoryPoint p)
        {
            return new Vector3d(p.velocity.x, p.velocity.y, p.velocity.z);
        }

        private static TwoBodyOrbit Solve(Vector3d posYup, Vector3d velYup)
        {
            Assert.True(TwoBodyOrbit.TryCreate(posYup.xzy, velYup.xzy, KerbinMu, 0.0, out TwoBodyOrbit orbit),
                "two-body solve failed");
            return orbit;
        }
    }
}
