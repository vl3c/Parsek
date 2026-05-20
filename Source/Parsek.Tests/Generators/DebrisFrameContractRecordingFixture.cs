using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    internal static class DebrisFrameContractRecordingFixture
    {
        internal sealed class Fixture
        {
            internal RecordingTree Tree;
            internal Recording Parent;
            internal Recording Debris;
            internal TrackSection AbsoluteSection;
            internal TrackSection RelativeSection;
        }

        internal static Fixture Create(bool loopAnchoredParent = false)
        {
            string treeId = loopAnchoredParent
                ? "fixture-v13-loop-tree"
                : "fixture-v13-tree";
            string parentId = loopAnchoredParent
                ? "fixture-v13-loop-parent"
                : "fixture-v13-parent";
            string debrisId = loopAnchoredParent
                ? "fixture-v13-loop-debris"
                : "fixture-v13-debris";
            uint loopPid = loopAnchoredParent ? 77u : 0u;

            var absoluteSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    Point(0.0, 10.0),
                    Point(10.0, 20.0),
                },
            };
            var relativeSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 10.0,
                endUT = 20.0,
                anchorRecordingId = parentId,
                frames = new List<TrajectoryPoint>
                {
                    Point(10.0, 1.0),
                    Point(20.0, 2.0),
                },
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    Point(10.0, 110.0),
                    Point(20.0, 120.0),
                },
            };
            var debris = new Recording
            {
                RecordingId = debrisId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                TreeId = treeId,
                VesselName = debrisId,
                PlaybackEnabled = true,
                IsDebris = true,
                ParentAnchorRecordingId = parentId,
                Points = new List<TrajectoryPoint>
                {
                    absoluteSection.frames[0],
                    absoluteSection.frames[1],
                    relativeSection.frames[0],
                    relativeSection.frames[1],
                },
                TrackSections = new List<TrackSection>
                {
                    absoluteSection,
                    relativeSection,
                },
            };
            var parent = new Recording
            {
                RecordingId = parentId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                TreeId = treeId,
                VesselName = parentId,
                LoopPlayback = loopAnchoredParent,
                LoopAnchorVesselId = loopPid,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = loopAnchoredParent
                            ? ReferenceFrame.Relative
                            : ReferenceFrame.Absolute,
                        startUT = 10.0,
                        endUT = 20.0,
                        anchorVesselId = loopPid,
                        frames = new List<TrajectoryPoint>
                        {
                            Point(10.0, 210.0),
                            Point(20.0, 220.0),
                        },
                    },
                },
            };
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = parentId,
                ActiveRecordingId = debrisId,
            };
            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(debris);

            return new Fixture
            {
                Tree = tree,
                Parent = parent,
                Debris = debris,
                AbsoluteSection = absoluteSection,
                RelativeSection = relativeSection,
            };
        }

        private static TrajectoryPoint Point(double ut, double latitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                bodyName = "Kerbin",
                latitude = latitude,
                longitude = 0.0,
                altitude = 0.0,
            };
        }
    }
}
