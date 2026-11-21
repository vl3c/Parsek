using System.Collections.Generic;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared recording builders for the optimizer split cells: back-to-back sections from a
    /// start UT, each carrying two frames and two flat points on its body, so the optimizer's
    /// body, duration and payload reads all see real data.
    /// </summary>
    internal static class OptimizerSplitShapes
    {
        internal static Recording Make(
            string recordingId,
            double startUT,
            params (SegmentEnvironment env, double duration, string body)[] sections)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = "Shape " + recordingId,
                MergeState = MergeState.Immutable,
                VesselPersistentId = 4242,
            };
            double cur = startUT;
            for (int i = 0; i < sections.Length; i++)
            {
                var (env, duration, body) = sections[i];
                double sectionStart = cur;
                double sectionEnd = cur + duration;
                double alt = env == SegmentEnvironment.Atmospheric ? 40000
                    : (env == SegmentEnvironment.SurfaceMobile || env == SegmentEnvironment.SurfaceStationary)
                        ? 70 : 90000;
                rec.Points.Add(new TrajectoryPoint { ut = sectionStart, altitude = alt, bodyName = body });
                rec.Points.Add(new TrajectoryPoint { ut = sectionEnd, altitude = alt, bodyName = body });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = env,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = sectionStart,
                    endUT = sectionEnd,
                    sampleRateHz = 10f,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = sectionStart, altitude = alt, bodyName = body },
                        new TrajectoryPoint { ut = sectionEnd, altitude = alt, bodyName = body },
                    },
                    checkpoints = new List<OrbitSegment>(),
                });
                cur = sectionEnd;
            }
            return rec;
        }

        internal static (SegmentEnvironment, double, string) Atmo(double d, string body = "Kerbin")
            => (SegmentEnvironment.Atmospheric, d, body);

        internal static (SegmentEnvironment, double, string) Exo(double d, string body = "Kerbin")
            => (SegmentEnvironment.ExoBallistic, d, body);

        internal static (SegmentEnvironment, double, string) Burn(double d, string body = "Kerbin")
            => (SegmentEnvironment.ExoPropulsive, d, body);

        internal static (SegmentEnvironment, double, string) Ground(double d, string body = "Kerbin")
            => (SegmentEnvironment.SurfaceMobile, d, body);

        internal static (SegmentEnvironment, double, string) Pad(double d, string body = "Kerbin")
            => (SegmentEnvironment.SurfaceStationary, d, body);

        internal static (SegmentEnvironment, double, string) Approach(double d, string body = "Mun")
            => (SegmentEnvironment.Approach, d, body);

        /// <summary>Commits <paramref name="rec"/> in its own tree, as the load path does.</summary>
        internal static Recording CommitAlone(Recording rec, string treeId = null)
        {
            rec.TreeId = treeId ?? ("tree-" + rec.RecordingId);
            var tree = new RecordingTree
            {
                Id = rec.TreeId,
                TreeName = rec.VesselName,
                RootRecordingId = rec.RecordingId,
            };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        /// <summary>The committed pieces of one tree, in chain order.</summary>
        internal static List<Recording> ChainOf(string treeId)
        {
            var list = new List<Recording>();
            foreach (var r in RecordingStore.CommittedRecordings)
                if (r.TreeId == treeId) list.Add(r);
            list.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            return list;
        }
    }
}
