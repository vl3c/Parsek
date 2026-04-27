using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards the invariant that on-rails BG-recorded vessels cannot trigger
    /// `RecordingOptimizer` env-class splits per-orbit, even when the orbit's periapsis
    /// dips into atmosphere.
    ///
    /// Background: an eccentric BG orbit with Pe inside Kerbin atmo would, in principle,
    /// classify each periapsis sample as `Atmospheric` and each apoapsis sample as
    /// `ExoBallistic`. If those classifications reached `Recording.TrackSections`, the
    /// optimizer's `FindSplitCandidatesForOptimizer` would split the recording into a
    /// chain of 2N segments after N orbits — unbounded for any long-coasting BG vessel.
    ///
    /// Three structural facts together prevent this:
    ///   1. `BackgroundOnRailsState` carries no TrackSection / EnvironmentHysteresis
    ///      fields (only `BackgroundVesselState` for loaded mode does).
    ///   2. `OnBackgroundPhysicsFrame` early-returns on `bgVessel.packed`, so the
    ///      env-classification path never runs while on rails.
    ///   3. `FindSplitCandidatesForOptimizer` iterates `rec.TrackSections` only and
    ///      ignores `rec.OrbitSegments` (which carry orbital elements, not env tags).
    ///
    /// These tests exercise (1) and (3) directly. Together they trip if any future
    /// refactor moves TrackSection state onto the on-rails struct or inverts the
    /// optimizer's splitting predicate.
    ///
    /// Related: `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16.
    /// </summary>
    [Collection("Sequential")]
    public class EccentricOrbitOptimizerInvariantTests : IDisposable
    {
        public EccentricOrbitOptimizerInvariantTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>
        /// Synthetic recording of an eccentric BG orbit grazing Kerbin atmosphere over
        /// 200 periapsis passes. Mirrors what BackgroundRecorder produces in practice:
        /// a stream of OrbitSegments (one per warp-rate-change checkpoint) with no
        /// TrackSections at all. The optimizer must produce zero split candidates.
        /// </summary>
        [Fact]
        public void OrbitSegmentsOnly_ProducesZeroSplitCandidates_Even_With_Hundreds_Of_Orbits()
        {
            const int orbitCount = 200;
            const double orbitalPeriod = 3600.0;

            var rec = new Recording
            {
                RecordingId = "rec_eccentric_grazing",
                VesselName = "BG Grazing Probe",
                ChainId = "chain_grazing",
                ChainIndex = 0,
                ChainBranch = 0
            };

            for (int i = 0; i < orbitCount; i++)
            {
                double startUT = 100.0 + i * orbitalPeriod;
                rec.OrbitSegments.Add(new OrbitSegment
                {
                    startUT = startUT,
                    endUT = startUT + orbitalPeriod,
                    bodyName = "Kerbin",
                    semiMajorAxis = 800000.0,
                    eccentricity = 0.18,
                    inclination = 0.05,
                    longitudeOfAscendingNode = 0.0,
                    argumentOfPeriapsis = 0.0,
                    meanAnomalyAtEpoch = 0.0,
                    epoch = startUT
                });
            }

            Assert.Empty(rec.TrackSections);

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(
                new List<Recording> { rec });

            Assert.Empty(candidates);
        }

        /// <summary>
        /// Recording carrying a single ExoBallistic OrbitalCheckpoint TrackSection (what
        /// `StartCheckpointTrackSection` emits when a loaded BG vessel transitions back
        /// to on-rails) plus many OrbitSegments. There is only one section, so even with
        /// thousands of orbits the optimizer has no boundary to split on.
        /// </summary>
        [Fact]
        public void SingleCheckpointTrackSection_With_Many_Orbits_Produces_No_Split_Candidates()
        {
            var rec = new Recording
            {
                RecordingId = "rec_single_checkpoint",
                VesselName = "BG Grazing Probe",
                ChainId = "chain_single",
                ChainIndex = 0,
                ChainBranch = 0
            };

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 100.0,
                endUT = 100.0 + 1000 * 3600.0,
                frames = new List<TrajectoryPoint>()
            });

            for (int i = 0; i < 1000; i++)
            {
                double startUT = 100.0 + i * 3600.0;
                rec.OrbitSegments.Add(new OrbitSegment
                {
                    startUT = startUT,
                    endUT = startUT + 3600.0,
                    bodyName = "Kerbin",
                    semiMajorAxis = 800000.0,
                    eccentricity = 0.18,
                    epoch = startUT
                });
            }

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(
                new List<Recording> { rec });

            Assert.Empty(candidates);
        }

        /// <summary>
        /// Sanity check: a recording with two real env-class TrackSections still
        /// triggers a split candidate. Otherwise the test above would pass for the
        /// wrong reason (e.g. if the optimizer were silently disabled).
        /// </summary>
        [Fact]
        public void Two_Different_Env_TrackSections_Still_Produce_Split_Candidate()
        {
            var rec = new Recording
            {
                RecordingId = "rec_real_env_change",
                VesselName = "Test Vessel",
                ChainId = "chain_real",
                ChainIndex = 0,
                ChainBranch = 0
            };

            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 199.0, altitude = 75000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 65000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 300.0, altitude = 30000, bodyName = "Kerbin" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 200.0,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 200.0,
                endUT = 300.0,
                frames = new List<TrajectoryPoint>()
            });

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(
                new List<Recording> { rec });

            Assert.Single(candidates);
            Assert.Equal((0, 1), candidates[0]);
        }

        /// <summary>
        /// Structural invariant: the on-rails BG state class must not carry any
        /// TrackSection / TrackSection-list / EnvironmentHysteresis fields. If a future
        /// refactor adds one, the per-orbit env-toggle failure mode resurfaces — every
        /// downstream guarantee in this file is built on the absence of these fields.
        /// </summary>
        [Fact]
        public void BackgroundOnRailsState_HasNo_TrackSection_Or_EnvironmentHysteresis_Field()
        {
            Type bgRecorderType = typeof(BackgroundRecorder);
            Type onRailsStateType = bgRecorderType.GetNestedType(
                "BackgroundOnRailsState",
                BindingFlags.NonPublic);

            Assert.NotNull(onRailsStateType);

            FieldInfo[] fields = onRailsStateType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (FieldInfo f in fields)
            {
                Assert.False(f.FieldType == typeof(TrackSection),
                    $"BackgroundOnRailsState gained a TrackSection field '{f.Name}'. " +
                    "This breaks the eccentric-orbit invariant — see XML doc on this class.");

                Assert.False(f.FieldType == typeof(List<TrackSection>),
                    $"BackgroundOnRailsState gained a List<TrackSection> field '{f.Name}'. " +
                    "This breaks the eccentric-orbit invariant — see XML doc on this class.");

                Assert.False(f.FieldType == typeof(EnvironmentHysteresis),
                    $"BackgroundOnRailsState gained an EnvironmentHysteresis field '{f.Name}'. " +
                    "This would let on-rails periapsis grazes emit env-class TrackSection toggles.");
            }
        }

        /// <summary>
        /// Cross-check: BackgroundVesselState (loaded-mode struct) DOES carry these
        /// fields. If this assertion fails, the reflection check above is targeting
        /// the wrong type and could silently pass for the wrong reason.
        /// </summary>
        [Fact]
        public void BackgroundVesselState_DoesCarry_TrackSection_Fields_Sanity_Check()
        {
            Type bgRecorderType = typeof(BackgroundRecorder);
            Type loadedStateType = bgRecorderType.GetNestedType(
                "BackgroundVesselState",
                BindingFlags.NonPublic);

            Assert.NotNull(loadedStateType);

            FieldInfo[] fields = loadedStateType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            bool hasTrackSection = fields.Any(f => f.FieldType == typeof(TrackSection));
            bool hasTrackSectionList = fields.Any(f => f.FieldType == typeof(List<TrackSection>));
            bool hasEnvHysteresis = fields.Any(f => f.FieldType == typeof(EnvironmentHysteresis));

            Assert.True(hasTrackSection,
                "Sanity-check failure: BackgroundVesselState should carry a TrackSection field. " +
                "If this changed, the on-rails invariant test above may be checking the wrong contrast.");
            Assert.True(hasTrackSectionList,
                "Sanity-check failure: BackgroundVesselState should carry a List<TrackSection> field.");
            Assert.True(hasEnvHysteresis,
                "Sanity-check failure: BackgroundVesselState should carry an EnvironmentHysteresis field.");
        }
    }
}
