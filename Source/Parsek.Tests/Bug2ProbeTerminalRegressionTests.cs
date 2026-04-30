using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug 2: Probe terminal regresses Splashed → SubOrbital across
    /// trim+reload+second-finalize.
    ///
    /// <para>Reproduces the 2026-04-30_2011_uf-stash-bug playtest. After a
    /// booster ("Kerbal X Probe") splashed in the ocean and the merge committed
    /// the tree, the optimizer split the recording at an Atmospheric→SurfaceMobile
    /// boundary. <c>RecordingOptimizer.SplitAtSection</c> moved the Splashed
    /// terminal to the new second-half recording and nulled the original's
    /// <c>TerminalStateValue</c>. A subsequent Re-Fly stripped the second-half
    /// (the chain successor) along with its terminal data, and the
    /// quickload-resume tree trim cropped the original recording's payload
    /// down to a single ascending start-point (UT=149.4, alt=52571m). On the
    /// next scene exit the live vessel was no longer findable, the trajectory
    /// inference defaulted to <see cref="TerminalState.SubOrbital"/> from the
    /// single high-altitude point, and the recording was misclassified into
    /// STASH as <c>stableLeafUnconcluded slot=1 terminal=SubOrbital</c>.</para>
    ///
    /// <para>Fix: extend
    /// <see cref="ParsekFlight.ShouldSkipSceneExitSurfaceInferenceForRestoredRecording"/>
    /// (and its
    /// <see cref="ParsekFlight.HasOnlySubOrbitalFallbackEvidence"/> helper) so
    /// the trajectory inference is skipped when the surviving payload would
    /// only support the SubOrbital default — i.e. fewer than 2 points AND no
    /// low-altitude point AND no SurfaceMobile/Stationary last section AND no
    /// closed orbit segment. Single-point genuine landings still hit the
    /// inference and stamp Landed.</para>
    /// </summary>
    [Collection("Sequential")]
    public class Bug2ProbeTerminalRegressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug2ProbeTerminalRegressionTests()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -- The user's exact case (the regression) --------------------------

        /// <summary>
        /// User scenario from logs/2026-04-30_2011_uf-stash-bug/KSP.log:15451.
        /// Probe recording <c>a98613b5…</c> had its terminal=Splashed nulled
        /// by an optimizer split, the chain successor (which held the Splashed
        /// terminal) was Re-Fly-stripped, and quickload-resume trim left a
        /// single ascending start-point at altitude=52571m. The live vessel
        /// is unloaded at scene exit. Finalize must NOT stamp SubOrbital onto
        /// the recording from the trajectory; it must leave terminal unset
        /// and log the structured skip line.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_TrimSurvivorWithSingleAscendingPoint_DoesNotInferSubOrbital()
        {
            var rec = new Recording
            {
                RecordingId = "a98613b5-probe-trim-survivor",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3016707386u,
                ChildBranchPointId = null, // leaf
                MaxDistanceFromLaunch = 0.0,
                // RestoredFromCommittedTreeThisFrame = false (no committed-tree repair this frame)
            };
            // The surviving point matches the user's log: ascending, altitude well
            // above the 50m Landed threshold, no orbit segment, no SurfaceMobile section.
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 149.44,
                altitude = 52571.12,
                bodyName = "Kerbin",
                latitude = -0.19,
                longitude = -65.44,
            });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 11809.0, isSceneExit: true);

            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("FinalizeTreeRecordings: skipping Landed/Splashed inference") &&
                l.Contains("a98613b5-probe-trim-survivor") &&
                l.Contains("trajectory has insufficient points for inference"));
            // Pin: the bogus inferred-SubOrbital line MUST NOT have fired.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("inferred SubOrbital from trajectory") &&
                l.Contains("a98613b5-probe-trim-survivor"));
        }

        /// <summary>
        /// Active-non-leaf variant of the same regression — the
        /// <see cref="ParsekFlight.EnsureActiveRecordingTerminalState"/> path
        /// also routes through <see cref="ParsekFlight.HasOnlySubOrbitalFallbackEvidence"/>
        /// via the same skip helper.
        /// </summary>
        [Fact]
        public void EnsureActiveRecordingTerminalState_TrimSurvivorWithSingleAscendingPoint_DoesNotInferSubOrbital()
        {
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-trim-survivor",
                VesselPersistentId = 3016707386u,
                ChildBranchPointId = "branch-1", // non-leaf so EnsureActive runs
                MaxDistanceFromLaunch = 0.0,
            };
            active.Points.Add(new TrajectoryPoint
            {
                ut = 149.44,
                altitude = 52571.12,
                bodyName = "Kerbin",
            });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: true);

            Assert.False(active.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("active recording 'active-trim-survivor'") &&
                l.Contains("trajectory has insufficient points for inference"));
        }

        // -- HasOnlySubOrbitalFallbackEvidence pure helper tests ---------------

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_NullRecording_ReturnsFalse()
        {
            Assert.False(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(null));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_TwoOrMorePoints_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "two-points" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 60000.0, bodyName = "Kerbin" });

            Assert.False(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointLowAltitude_ReturnsFalse()
        {
            // Single point at altitude<50 is real Landed evidence — the inference
            // must still run for genuine single-point landings.
            var rec = new Recording { RecordingId = "single-low-alt" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5.0, bodyName = "Kerbin" });

            Assert.False(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointHighAltitudeSurfaceMobileSection_ReturnsFalse()
        {
            // A SurfaceMobile/Stationary last section is real Landed evidence
            // even with a single high-altitude point.
            var rec = new Recording { RecordingId = "surface-mobile-section" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.SurfaceMobile });

            Assert.False(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointHighAltitudeStableOrbit_ReturnsFalse()
        {
            // A closed-orbit segment is real Orbiting evidence even with a
            // single high-altitude point.
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting =
                bodyName => bodyName == "Kerbin" ? 600000.0 : (double?)null;
            var rec = new Recording { RecordingId = "stable-orbit" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.05,
            });

            Assert.False(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointHighAltitudeBodyUnresolvedOrbit_ReturnsTrue()
        {
            // Orbit evidence only counts when the inference path can resolve the
            // body radius and prove periapsis is above the surface.
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = _ => null;
            var rec = new Recording { RecordingId = "unresolved-orbit-body" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.05,
            });

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointHighAltitudeSurfaceIntersectingOrbit_ReturnsTrue()
        {
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting =
                bodyName => bodyName == "Kerbin" ? 600000.0 : (double?)null;
            var rec = new Recording { RecordingId = "surface-intersecting-orbit" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 500000.0,
                eccentricity = 0.10,
            });

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointHighAltitudeNoSignals_ReturnsTrue()
        {
            // The user's exact regression shape: single point at high altitude,
            // no SurfaceMobile/Stationary section, no stable orbit. Inference
            // would default to SubOrbital — refuse.
            var rec = new Recording { RecordingId = "the-bug-shape" };
            rec.Points.Add(new TrajectoryPoint { ut = 149.44, altitude = 52571.12, bodyName = "Kerbin" });

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_ZeroPoints_ReturnsTrue()
        {
            // No points at all — InferTerminalStateFromTrajectory returns
            // SubOrbital by its initial null/empty check. Same fabricated guess.
            var rec = new Recording { RecordingId = "no-points" };

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_ZeroPointsWithStableOrbit_ReturnsTrue()
        {
            // InferTerminalStateFromTrajectory returns SubOrbital before it
            // inspects orbit segments when there are zero points.
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting =
                bodyName => bodyName == "Kerbin" ? 600000.0 : (double?)null;
            var rec = new Recording { RecordingId = "no-points-stable-orbit" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.05,
            });

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        [Fact]
        public void HasOnlySubOrbitalFallbackEvidence_SinglePointEscapingOrbit_ReturnsTrue()
        {
            // An open orbit (eccentricity >= 1) does not count as Orbiting
            // evidence — InferTerminalStateFromTrajectory would still fall
            // through to SubOrbital. Refuse.
            var rec = new Recording { RecordingId = "escaping-orbit" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 1.5, // hyperbolic
            });

            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }

        // -- Round-trip through the regression sequence -----------------------

        /// <summary>
        /// Simulates the full sequence that produced the original regression:
        ///   1. First finalize stamps Splashed.
        ///   2. Optimizer-style split nulls the original's TerminalStateValue
        ///      (the new second-half retains it).
        ///   3. Re-Fly-style strip drops the second-half from the active tree.
        ///   4. Quickload-resume tree trim cuts everything past the cutoff,
        ///      leaving a single ascending start-point.
        ///   5. Second scene exit finalize: with the fix, terminal stays unset.
        ///      Without the fix, it would silently regress to SubOrbital.
        /// </summary>
        [Fact]
        public void SplitThenStripThenTrimThenSecondFinalize_PreservesNullTerminal()
        {
            // 1. Recording starts in the user-observed first-finalize shape:
            //    693 points, terminal=Splashed.
            var rec = new Recording
            {
                RecordingId = "round-trip-probe",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3016707386u,
                ChildBranchPointId = null,
                TerminalStateValue = TerminalState.Splashed,
            };
            for (int i = 0; i < 10; i++)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 149.44 + i * 50.0,
                    altitude = 52571.12 - i * 5000.0,
                    bodyName = "Kerbin",
                });
            }

            // 2. Optimizer split: the original recording loses its
            //    TerminalStateValue (the second half gets it). Mirror the
            //    RecordingOptimizer.SplitAtSection contract on line 800.
            rec.TerminalStateValue = null;

            // 3. Re-Fly strip removes the second-half from the tree (we
            //    don't model the second-half here — what matters is that the
            //    surviving recording has no chain successor with a terminal).
            // (no-op in this test: there's no second-half to remove.)

            // 4. Quickload-resume trim past UT=150.30 leaves only the start
            //    point.
            ParsekScenario.TrimRecordingPastUT(rec, cutoffUT: 150.30);
            Assert.Single(rec.Points);
            Assert.True(rec.Points[0].altitude > 50.0); // high-altitude survivor

            // 5. Second scene exit finalize: with the fix, terminal stays unset.
            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 11809.0, isSceneExit: true);

            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("round-trip-probe"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("inferred SubOrbital from trajectory") &&
                l.Contains("round-trip-probe"));
        }
    }
}
