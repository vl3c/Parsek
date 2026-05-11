using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SegmentPhasePersistenceTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SegmentPhasePersistenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void ClassifyFromValues_MatchesSegmentPhaseVocabulary()
        {
            Assert.Equal("surface", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.LANDED, true, 0, 70000, 10000));
            Assert.Equal("surface", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.SPLASHED, true, 0, 70000, 10000));
            Assert.Equal("surface", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.PRELAUNCH, true, 0, 70000, 10000));
            Assert.Equal("atmo", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.FLYING, true, 1000, 70000, 10000));
            Assert.Equal("exo", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.FLYING, true, 70000, 70000, 10000));
            Assert.Equal("approach", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.SUB_ORBITAL, false, 9999, 0, 10000));
            Assert.Equal("exo", SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.SUB_ORBITAL, false, 10000, 0, 10000));
        }

        [Fact]
        public void ApplyFinalSegmentPhaseFromCapture_UsesActiveRowNotCaptureId()
        {
            var treeRec = new Recording
            {
                RecordingId = "tree-rec",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin"
            };
            var capture = new Recording
            {
                RecordingId = "fresh-capture-guid",
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin"
            };

            bool applied = ParsekFlight.ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                activeRecordingId: "tree-rec",
                captureAtStop: capture);

            Assert.True(applied);
            Assert.Equal("exo", treeRec.SegmentPhase);
            Assert.Equal("Kerbin", treeRec.SegmentBodyName);
        }

        [Fact]
        public void ApplyFinalSegmentPhaseFromCapture_NoCapturePhase_PreservesExistingTag()
        {
            var treeRec = new Recording
            {
                RecordingId = "tree-rec",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin"
            };
            var capture = new Recording
            {
                RecordingId = "fresh-capture-guid"
            };

            bool applied = ParsekFlight.ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                activeRecordingId: "tree-rec",
                captureAtStop: capture);

            Assert.False(applied);
            Assert.Equal("atmo", treeRec.SegmentPhase);
            Assert.Equal("Kerbin", treeRec.SegmentBodyName);
        }

        [Fact]
        public void ApplyFinalSegmentPhaseFromCapture_CommittedChainSegment_PreservesExistingTag()
        {
            var treeRec = new Recording
            {
                RecordingId = "tree-rec",
                ChainId = "chain",
                ChainIndex = 0,
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin"
            };
            var capture = new Recording
            {
                RecordingId = "fresh-capture-guid",
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin"
            };

            bool applied = ParsekFlight.ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                activeRecordingId: "tree-rec",
                captureAtStop: capture);

            Assert.False(applied);
            Assert.Equal("atmo", treeRec.SegmentPhase);
        }

        [Fact]
        public void ApplyFinalSegmentPhaseFromCapture_NonActiveOptimizerRow_PreservesExistingTag()
        {
            var treeRec = new Recording
            {
                RecordingId = "split-rec",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.Atmospheric,
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 0,
                        endUT = 10
                    }
                }
            };
            var capture = new Recording
            {
                RecordingId = "fresh-capture-guid",
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin"
            };

            bool applied = ParsekFlight.ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                activeRecordingId: "other-active-rec",
                captureAtStop: capture);

            Assert.False(applied);
            Assert.Equal("atmo", treeRec.SegmentPhase);
        }

        [Fact]
        public void FinalizeIndividualRecording_ForceStopNoCapture_AppliesTerminalOrbitPhase()
        {
            var rec = new Recording
            {
                RecordingId = "active",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };
            var tree = ActiveTree(rec);

            ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 100,
                isSceneExit: true,
                treeContext: tree);

            Assert.Equal("exo", rec.SegmentPhase);
            Assert.Equal("Kerbin", rec.SegmentBodyName);
        }

        [Fact]
        public void FinalizeIndividualRecording_PreExistingTerminalState_StillAppliesEndpointPhase()
        {
            var rec = new Recording
            {
                RecordingId = "active",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };
            var tree = ActiveTree(rec);

            ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 100,
                isSceneExit: false,
                treeContext: tree);

            Assert.Equal("exo", rec.SegmentPhase);
        }

        [Fact]
        public void FinalizeIndividualRecording_RelativeEndpointWithStaleBodyTags_PreservesAndLogs()
        {
            var rec = new Recording
            {
                RecordingId = "active",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                StartBodyName = "Kerbin",
                EndpointBodyName = "Kerbin",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        latitude = 1234,
                        longitude = 5678,
                        altitude = 9012,
                        bodyName = "Kerbin"
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.ExoBallistic,
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 0,
                        endUT = 100,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint
                            {
                                ut = 100,
                                latitude = 1234,
                                longitude = 5678,
                                altitude = 9012,
                                bodyName = "Kerbin"
                            }
                        }
                    }
                }
            };
            var tree = ActiveTree(rec);

            ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 100,
                isSceneExit: true,
                treeContext: tree);

            Assert.Equal("atmo", rec.SegmentPhase);
            Assert.Equal("Kerbin", rec.SegmentBodyName);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Flight]")
                && l.Contains("Final SegmentPhase skipped")
                && l.Contains("relative-endpoint-unresolved"));
        }

        [Fact]
        public void ApplyFinalEndpointSegmentPhase_CommittedChainSegment_PreservesExistingTag()
        {
            var rec = new Recording
            {
                RecordingId = "active",
                ChainId = "chain",
                ChainIndex = 0,
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };
            var tree = ActiveTree(rec);

            bool applied = ParsekFlight.ApplyFinalEndpointSegmentPhase(
                rec,
                tree,
                finalizeVesselFound: false,
                finalizeVessel: null);

            Assert.False(applied);
            Assert.Equal("atmo", rec.SegmentPhase);
        }

        [Fact]
        public void ApplyFinalEndpointSegmentPhase_NonActiveOptimizerRow_PreservesExistingTag()
        {
            var rec = new Recording
            {
                RecordingId = "split-rec",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.Atmospheric,
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 0,
                        endUT = 10
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree",
                RootRecordingId = "root",
                ActiveRecordingId = "other-active-rec"
            };
            tree.Recordings[rec.RecordingId] = rec;

            bool applied = ParsekFlight.ApplyFinalEndpointSegmentPhase(
                rec,
                tree,
                finalizeVesselFound: false,
                finalizeVessel: null);

            Assert.False(applied);
            Assert.Equal("atmo", rec.SegmentPhase);
        }

        private static RecordingTree ActiveTree(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = "tree",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }
    }
}
