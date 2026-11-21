using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Flight asr-final (lane ASR-1): the probe-cored core, a controlled background child on
    /// rails and UNLOADED (beyond the load range) at 93.7 km on a sub-orbital arc, was finalized
    /// at the scene exit by the no-solver fallback as Destroyed at the exit UT with no predicted
    /// tail. Two causes: the liveness read counted only LOADED parts, and the tail a reseeded
    /// recovery built started inside the recorded payload, which the commit merge drops.
    /// </summary>
    [Collection("Sequential")]
    public class AbandonedStageNoSolverTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double ExitUT = 279.819;
        private const double TerminalUT = 427.744;

        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<string> logLines = new List<string>();

        public AbandonedStageNoSolverTests()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>A packed, unloaded vessel Unity's == reads as alive: stock Vessel.Unload
        /// leaves no loaded parts, its parts live in the protoVessel.</summary>
        private static Vessel UnloadedOnRailsVessel(int protoParts, Vessel.State state)
        {
            var v = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            Assert.NotNull(CachedPtr);
            CachedPtr.SetValue(v, new IntPtr(1));
            v.persistentId = 3493522029u;
            v.vesselName = "Kerbal X Probe";
            v.loaded = false;
            v.packed = true;
            v.state = state;
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot>();
            for (int i = 0; i < protoParts; i++)
                pv.protoPartSnapshots.Add((ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot)));
            v.protoVessel = pv;
            return v;
        }

        private static ExtrapolationBody MakeKerbin()
        {
            return new ExtrapolationBody
            {
                Name = "Kerbin",
                GravitationalParameter = KerbinGravParameter,
                Radius = KerbinRadius,
                AtmosphereDepth = 70000.0,
                DeletionAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude,
                SphereOfInfluence = 84159286.0
            };
        }

        [Fact]
        public void IsLiveVesselAlive_UnloadedOnRailsVesselWithProtoParts_IsAlive()
        {
            Vessel probe = UnloadedOnRailsVessel(13, Vessel.State.INACTIVE);

            Assert.True(IncompleteBallisticSceneExitFinalizer.IsLiveVesselAlive(probe));
        }

        [Fact]
        public void IsLiveVesselAlive_DeadUnloadedVessel_IsNotAlive()
        {
            Vessel probe = UnloadedOnRailsVessel(13, Vessel.State.DEAD);

            Assert.False(IncompleteBallisticSceneExitFinalizer.IsLiveVesselAlive(probe));
        }

        [Fact]
        public void IsVesselAliveCore_Table()
        {
            // loaded with parts
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(false, true, 13, 0));
            // unloaded: parts only in the protoVessel
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(false, false, 0, 13));
            // dead, either way
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(true, true, 13, 13));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(true, false, 0, 13));
            // loaded with no parts left (torn down)
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(false, true, 0, 13));
            // unloaded with no proto parts
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsVesselAliveCore(false, false, 0, 0));
        }

        /// <summary>
        /// The flight's exact shape: a solver-less (NullSolver), unloaded, on-rails vessel at
        /// 93.7 km near the top of a sub-orbital arc (sma 366.8 km, ecc 0.894) at the scene
        /// exit. Its liveness read off the unloaded vessel must let it follow its live orbit
        /// to the ground (Q2), not be classified Destroyed at the exit UT.
        /// </summary>
        [Fact]
        public void SceneExit_UnloadedSolverlessProbeAt93Km_FallsToTheGround()
        {
            Vessel probe = UnloadedOnRailsVessel(13, Vessel.State.INACTIVE);
            bool alive = IncompleteBallisticSceneExitFinalizer.IsLiveVesselAlive(probe);

            var rec = new Recording
            {
                RecordingId = "asr1-probe-unloaded",
                ExplicitStartUT = 132.9,
                ExplicitEndUT = ExitUT
            };
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };
            double r = KerbinRadius + 93734.1;
            // Near apoapsis of the recorded conic: mostly horizontal, slowly descending.
            double vHorizontal = Math.Sqrt(KerbinGravParameter * (2.0 / r - 1.0 / 366829.2));

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                new PatchedConicSnapshotResult
                {
                    FailureReason = PatchedConicSnapshotFailureReason.NullSolver,
                    Segments = new List<OrbitSegment>()
                },
                bodies,
                delegate(out BallisticStateVector startState)
                {
                    startState = new BallisticStateVector
                    {
                        ut = ExitUT,
                        bodyName = "Kerbin",
                        position = new Vector3d(r, 0.0, 0.0),
                        velocity = new Vector3d(-20.0, vHorizontal, 0.0),
                        orbitalFrameRotation = Quaternion.identity
                    };
                    return true;
                },
                (startState, extrapolationBodies) => BallisticExtrapolator.Extrapolate(
                    startState, extrapolationBodies, warnOnSubSurfaceStart: false),
                null,
                alive,
                Vector3.zero,
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.NotEqual(ExtrapolationFailureReason.NoSolverStart, result.extrapolationFailureReason);
            Assert.True(result.terminalUT > ExitUT + 60.0, "terminalUT=" + result.terminalUT);
            Assert.NotNull(result.appendedOrbitSegments);
            Assert.NotEmpty(result.appendedOrbitSegments);
            Assert.True(result.appendedOrbitSegments[0].startUT >= ExitUT - 1e-6);
            Assert.Contains(logLines, l => l.Contains("solver-less live vessel 'asr1-probe-unloaded' follows its live orbit"));
            Assert.DoesNotContain(logLines, l => l.Contains("no-solver live-orbit fallback"));
        }

        // ------------------------------------------------- predicted tail across commit

        private static TrajectoryPoint Pt(double ut, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = -0.08,
                longitude = -73.3 + (ut - 132.9) * 0.01,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 900f, 0f)
            };
        }

        private static OrbitSegment Conic(double startUT, double endUT, bool predicted)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                inclination = 0.1,
                eccentricity = 0.8942,
                semiMajorAxis = 366829.2,
                longitudeOfAscendingNode = 10.0,
                argumentOfPeriapsis = 20.0,
                meanAnomalyAtEpoch = 3.0,
                epoch = 220.459,
                isPredicted = predicted,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero
            };
        }

        /// <summary>The probe recording as the scene exit left it, before its tail is
        /// applied: an atmospheric and a space Absolute section from the background
        /// recorder, then the on-rails checkpoint up to the exit UT.</summary>
        private static Recording ProbeShapedRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X Probe",
                TreeId = "asr1-tree",
                VesselPersistentId = 3493522029u,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var atmo = new List<TrajectoryPoint>();
            for (double ut = 132.9; ut < 174.4; ut += 2.0)
                atmo.Add(Pt(ut, 39660.0 + (ut - 132.9) * 700.0));
            // The recorder closes one section and opens the next on the same sample.
            atmo.Add(Pt(174.468, 70000.0));
            var space = new List<TrajectoryPoint>();
            for (double ut = 174.468; ut < 220.4; ut += 4.0)
                space.Add(Pt(ut, 70000.0 + (ut - 174.468) * 400.0));
            space.Add(Pt(220.459, 89452.3));

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = 132.9,
                endUT = 174.468,
                frames = atmo,
                sampleRateHz = 5f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = 174.468,
                endUT = 220.459,
                frames = space,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            OrbitSegment onRails = Conic(220.459, ExitUT, predicted: false);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 220.459,
                endUT = ExitUT,
                checkpoints = new List<OrbitSegment> { onRails },
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            });
            rec.Points.AddRange(atmo);
            rec.Points.AddRange(space.Skip(1));
            rec.OrbitSegments.Add(onRails);
            return rec;
        }

        private static IncompleteBallisticSceneExitFinalizer.TryFinalizeDelegate RecoveredTailStartingAt(
            double tailStartUT)
        {
            return (Recording recording, Vessel vessel, double commitUT,
                out IncompleteBallisticFinalizationResult result) =>
            {
                result = new IncompleteBallisticFinalizationResult
                {
                    appendedOrbitSegments = new List<OrbitSegment> { Conic(tailStartUT, TerminalUT, predicted: true) },
                    terminalState = TerminalState.Destroyed,
                    terminalUT = TerminalUT,
                    extrapolatedSegmentCount = 1
                };
                return true;
            };
        }

        /// <summary>
        /// The flight's second loss: the recovered tail was reseeded from a recorded point at
        /// UT 177.3, inside the recorded payload (which runs to the exit UT 279.8). The
        /// scene-exit apply must start it at the last authored UT, as the cache apply does.
        /// </summary>
        [Fact]
        public void SceneExitApply_TailStartingInsideRecordedPayload_StartsAtLastAuthoredUT()
        {
            Recording rec = ProbeShapedRecording("asr1-probe-apply");
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting = RecoveredTailStartingAt(177.299);

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryApply(rec, null, ExitUT, "FinalizeIndividualRecording"));

            List<OrbitSegment> predicted = rec.OrbitSegments.Where(s => s.isPredicted).ToList();
            Assert.Single(predicted);
            Assert.Equal(ExitUT, predicted[0].startUT, 6);
            Assert.Equal(TerminalUT, predicted[0].endUT, 6);
            Assert.Equal(0.8942, predicted[0].eccentricity);
            Assert.Contains(logLines, l => l.Contains("predicted tail clipped to the last authored UT")
                && l.Contains("asr1-probe-apply") && l.Contains("clipped=1"));
        }

        [Fact]
        public void SceneExitApply_TailStartingAtPayloadEnd_IsUnchanged()
        {
            Recording rec = ProbeShapedRecording("asr1-probe-apply-clean");
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting = RecoveredTailStartingAt(ExitUT);

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryApply(rec, null, ExitUT, "FinalizeIndividualRecording"));

            OrbitSegment predicted = rec.OrbitSegments.Single(s => s.isPredicted);
            Assert.Equal(ExitUT, predicted.startUT, 6);
            Assert.DoesNotContain(logLines, l => l.Contains("predicted tail clipped to the last authored UT"));
        }

        /// <summary>
        /// End to end over the commit: scene-exit apply, the tree merge (where the flight lost
        /// the tail: flatSync=track-sections) and the optimizer's atmosphere / space split.
        /// The space half must end Destroyed at the impact UT with the predicted tail.
        /// </summary>
        [Fact]
        public void Commit_ProbeWithTailFromEarlierRecordedPoint_SecondSplitHalfCarriesTheTail()
        {
            Recording rec = ProbeShapedRecording("asr1-probe-commit");
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting = RecoveredTailStartingAt(177.299);
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryApply(rec, null, ExitUT, "FinalizeIndividualRecording"));

            var tree = new RecordingTree { Id = "asr1-tree", TreeName = "Kerbal X" };
            tree.Recordings[rec.RecordingId] = rec;
            Dictionary<string, Recording> mergedById = SessionMerger.MergeTree(tree);
            Recording merged = mergedById[rec.RecordingId];

            Assert.Contains(merged.OrbitSegments, s => s.isPredicted && Math.Abs(s.endUT - TerminalUT) < 1e-6);
            Assert.DoesNotContain(logLines, l => l.EndsWith("MergeTree: recording='asr1-probe-commit' flatSync=track-sections"));

            int spaceIndex = merged.TrackSections.FindIndex(s => Math.Abs(s.startUT - 174.468) < 1e-6);
            Assert.True(spaceIndex > 0);
            Recording second = RecordingOptimizer.SplitAtSection(merged, spaceIndex);

            Assert.NotNull(second);
            Assert.Equal(TerminalState.Destroyed, second.TerminalStateValue);
            Assert.Equal(TerminalUT, second.EndUT, 3);
            Assert.Contains(second.OrbitSegments, s => s.isPredicted && Math.Abs(s.endUT - TerminalUT) < 1e-6);
            Assert.DoesNotContain(merged.OrbitSegments, s => s.isPredicted);
        }
    }
}
