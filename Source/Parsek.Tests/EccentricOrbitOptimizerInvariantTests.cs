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
    /// Four structural facts together prevent this:
    ///   1. `BackgroundOnRailsState` carries no TrackSection / EnvironmentHysteresis
    ///      fields (only `BackgroundVesselState` for loaded mode does).
    ///   2. `OnBackgroundPhysicsFrame` early-returns on `bgVessel.packed`, so the
    ///      env-classification path never runs while on rails.
    ///   3. Packed/on-rails closes may emit only OrbitalCheckpoint/ExoBallistic
    ///      TrackSections, never per-orbit Atmospheric/ExoBallistic toggles.
    ///   4. Same-class adjacent checkpoint sections are not splittable boundaries,
    ///      including ExoBallistic SOI transitions that #547 keeps cohesive.
    ///
    /// These tests exercise (1), (3), and (4) directly. Together they trip if any future
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

        private static TrackSection MakeCheckpointSection(double startUT, double endUT, string bodyName)
        {
            var segment = new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = 800000.0,
                eccentricity = 0.18,
                epoch = startUT
            };

            return RecordingStore.BuildClosedOnRailsCheckpointSection(segment);
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

        [Fact]
        public void N_OnRails_Closes_Same_Body_Same_Env_Produce_Zero_Split_Candidates()
        {
            const int checkpointCount = 200;
            const double segmentDuration = 3600.0;
            var rec = new Recording
            {
                RecordingId = "rec_many_checkpoint_closes",
                VesselName = "BG Grazing Probe",
                ChainId = "chain_many",
                ChainIndex = 0,
                ChainBranch = 0
            };

            for (int i = 0; i < checkpointCount; i++)
            {
                double startUT = 100.0 + i * segmentDuration;
                TrackSection section = MakeCheckpointSection(startUT, startUT + segmentDuration, "Kerbin");
                rec.TrackSections.Add(section);
                rec.OrbitSegments.Add(section.checkpoints[0]);
            }

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(
                new List<Recording> { rec });

            Assert.Empty(candidates);
        }

        [Fact]
        public void OnRails_Checkpoint_Body_Change_StaysCohesive()
        {
            var rec = new Recording
            {
                RecordingId = "rec_checkpoint_soi",
                VesselName = "BG SOI Probe",
                ChainId = "chain_soi",
                ChainIndex = 0,
                ChainBranch = 0
            };

            TrackSection kerbin = MakeCheckpointSection(100.0, 300.0, "Kerbin");
            TrackSection mun = MakeCheckpointSection(300.0, 500.0, "Mun");
            rec.TrackSections.Add(kerbin);
            rec.TrackSections.Add(mun);
            rec.OrbitSegments.Add(kerbin.checkpoints[0]);
            rec.OrbitSegments.Add(mun.checkpoints[0]);

            var logLines = new List<string>();
            List<(int, int)> candidates;
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(
                    new List<Recording> { rec });
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }

            Assert.Empty(candidates);
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]") &&
                l.Contains("Split summary") &&
                l.Contains("exoCoastBodyChangeKept=1"));
        }

        [Fact]
        public void SplitAtSection_ClipsOverlappingCheckpointBridgeAtSplitBoundary()
        {
            const double checkpointStartUT = 909.023508884545;
            const double splitUT = 958.87146746423491;
            const double checkpointEndUT = 960.51459653102938;
            TrackSection checkpoint = MakeCheckpointSection(
                checkpointStartUT, checkpointEndUT, "Kerbin");
            var physical = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = splitUT,
                endUT = 979.7714674642159,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = splitUT, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 979.7714674642159, bodyName = "Kerbin" }
                },
                checkpoints = new List<OrbitSegment>()
            };
            var rec = new Recording
            {
                RecordingId = "rec_split_checkpoint_overlap",
                VesselName = "Kerbal X Probe",
                ChainId = "chain_split",
                ChainIndex = 0,
                ChainBranch = 0,
                TrackSections = new List<TrackSection> { checkpoint, physical },
                OrbitSegments = new List<OrbitSegment> { checkpoint.checkpoints[0] }
            };

            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Single(rec.TrackSections);
            Assert.Equal(splitUT, rec.TrackSections[0].endUT);
            Assert.Single(rec.TrackSections[0].checkpoints);
            Assert.Equal(splitUT, rec.TrackSections[0].checkpoints[0].endUT);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(splitUT, rec.OrbitSegments[0].endUT);
            Assert.Single(second.TrackSections);
            Assert.Equal(splitUT, second.TrackSections[0].startUT);
            Assert.Empty(second.OrbitSegments);
        }

        [Fact]
        public void SplitAtSection_ClipsEarlierCheckpointBridgeThatWrapsSplitBoundary()
        {
            const double splitUT = 200.0;
            TrackSection wrappingCheckpoint = MakeCheckpointSection(100.0, 300.0, "Kerbin");
            TrackSection containedCheckpoint = MakeCheckpointSection(150.0, 160.0, "Kerbin");
            TrackSection splitCheckpoint = MakeCheckpointSection(splitUT, 240.0, "Kerbin");
            var rec = new Recording
            {
                RecordingId = "rec_split_earlier_checkpoint_overlap",
                VesselName = "Kerbal X Probe",
                ChainId = "chain_split_earlier",
                ChainIndex = 0,
                ChainBranch = 0,
                TrackSections = new List<TrackSection>
                {
                    wrappingCheckpoint,
                    containedCheckpoint,
                    splitCheckpoint
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    wrappingCheckpoint.checkpoints[0],
                    containedCheckpoint.checkpoints[0],
                    splitCheckpoint.checkpoints[0]
                }
            };

            Recording second = RecordingOptimizer.SplitAtSection(rec, 2);

            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(splitUT, rec.TrackSections[0].endUT);
            Assert.Single(rec.TrackSections[0].checkpoints);
            Assert.Equal(splitUT, rec.TrackSections[0].checkpoints[0].endUT);
            Assert.Equal(2, rec.OrbitSegments.Count);
            Assert.Equal(splitUT, rec.OrbitSegments[0].endUT);
            Assert.Single(second.TrackSections);
            Assert.Equal(splitUT, second.TrackSections[0].startUT);
            Assert.Single(second.OrbitSegments);
            Assert.Equal(splitUT, second.OrbitSegments[0].startUT);
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
