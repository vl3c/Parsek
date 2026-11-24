using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>OrbitSegmentCheckpointBridgeStats.Changed</c> reports a change only when the pass
    /// left the recording different. A checkpoint whose own span is wider than its section's
    /// (a coast that began before the section opened) is "covered" on every pass, and the flat
    /// orbit rebuild that asks for puts the same list back: counting it as a change dirtied
    /// the recording on every optimization pass (todo CHECKPOINT-BRIDGE-COVERED-IS-NOT-A-CHANGE).
    /// </summary>
    [Collection("Sequential")]
    public class CheckpointBridgeChangedTests : IDisposable
    {
        private const string MutatedLine = "FindSplitCandidatesForOptimizer: normalization Ensure mutated";

        private readonly List<string> logLines = new List<string>();
        private readonly bool prevSuppress;

        public CheckpointBridgeChangedTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
        }

        private static OrbitSegment Segment(double startUT, double endUT, double sma = 700000)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                eccentricity = 0.1,
                semiMajorAxis = sma,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                bodyName = "Kerbin"
            };
        }

        /// <summary>
        /// The recorded shape (harness fixture cs-career-mun2, recording 23e74057): one coast
        /// covers two checkpoint sections, and the later section holds the whole coast, so its
        /// checkpoint starts before the section does.
        /// </summary>
        private static Recording WideCheckpointRecording(string id, double sma = 700000)
        {
            OrbitSegment lead = Segment(1000, 1200, sma);
            OrbitSegment wide = Segment(1000, 2000, sma);
            TrackSection tail = OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(wide);
            tail.startUT = 1200;
            return new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(lead),
                    tail
                },
                OrbitSegments = new List<OrbitSegment> { lead, wide },
                FilesDirty = false
            };
        }

        [Fact]
        public void Ensure_CheckpointWiderThanItsSection_IsCoveredButNotAChange()
        {
            Recording rec = WideCheckpointRecording("wide-checkpoint");
            List<OrbitSegment> flat = rec.OrbitSegments;
            OrbitSegment[] flatBefore = flat.ToArray();
            var cached = new RecordingStats();
            rec.CachedStats = cached;

            for (int pass = 0; pass < 3; pass++)
            {
                var stats = OrbitSegmentCheckpointBridge
                    .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

                Assert.Equal(1, stats.SkippedCovered);
                Assert.Equal(0, stats.FlatOrbitCacheChanged);
                Assert.False(stats.Changed);
                Assert.False(stats.AnyMutation);
            }

            Assert.False(rec.FilesDirty);
            Assert.True(rec.CachedStats.HasValue);
            Assert.Same(flat, rec.OrbitSegments);
            Assert.Equal(flatBefore, rec.OrbitSegments);
            Assert.Equal(2, rec.TrackSections.Count);
        }

        // A NaN semi-major axis (parabolic conic) must compare equal to itself, or the pass
        // would report a change forever for that conic alone.
        [Fact]
        public void Ensure_WideNaNCheckpoint_IsNotAChange()
        {
            Recording rec = WideCheckpointRecording("wide-nan", double.NaN);

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

            Assert.True(stats.SkippedCovered > 0);
            Assert.False(stats.AnyMutation);
            Assert.False(rec.FilesDirty);
        }

        // The mirror: a covered envelope the rebuild DROPS is a real change, once.
        [Fact]
        public void Ensure_CoveredEnvelopeDroppedFromFlatCache_IsAChangeOnce()
        {
            OrbitSegment a = Segment(1000, 2000);
            OrbitSegment b = Segment(2000, 3000, sma: 800000);
            var rec = new Recording
            {
                RecordingId = "covered-envelope",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(a),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(b)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(1000, 3000, sma: 900000) },
                FilesDirty = false
            };

            var first = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

            Assert.Equal(0, first.Added);
            Assert.Equal(0, first.Clipped);
            Assert.Equal(1, first.FlatOrbitCacheChanged);
            Assert.True(first.Changed);
            Assert.True(rec.FilesDirty);
            Assert.Equal(new[] { 700000.0, 800000.0 },
                rec.OrbitSegments.Select(s => s.semiMajorAxis).ToArray());

            rec.FilesDirty = false;
            var second = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

            Assert.False(second.AnyMutation);
            Assert.False(rec.FilesDirty);
        }

        // Same count, different content: the flat list is compared by value, not by length.
        [Fact]
        public void Ensure_CoveredSegmentReplacedByTheSectionsOwnConic_IsAChange()
        {
            OrbitSegment owned = Segment(1000, 2000);
            var rec = new Recording
            {
                RecordingId = "covered-replaced",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(owned)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(1000, 2000, sma: 800000) },
                FilesDirty = false
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

            Assert.Equal(1, stats.SkippedCovered);
            Assert.True(stats.Changed);
            Assert.True(rec.FilesDirty);
            Assert.Equal(700000.0, Assert.Single(rec.OrbitSegments).semiMajorAxis);
        }

        [Fact]
        public void OptimizerAnalysisPass_OverNormalizedWideCheckpoint_DirtiesAndLogsNothing()
        {
            Recording rec = WideCheckpointRecording("wide-optimizer");
            var committed = new List<Recording> { rec };

            for (int pass = 0; pass < 2; pass++)
            {
                RecordingOptimizer.FindSplitCandidatesForOptimizer(committed);
                Assert.False(rec.FilesDirty);
            }

            Assert.DoesNotContain(logLines, l => l.Contains(MutatedLine));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("recording=wide-optimizer"));
        }

        // Every recorded trajectory under harness/fixtures/saves settles: after one write-path
        // Ensure, a second pass reports nothing. 50 of 610 never settled before the fix.
        [Fact]
        public void CommittedFixtures_SecondEnsurePass_ReportsNoChange()
        {
            RecordingStore.SuppressLogging = true;
            string saves = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves"));
            string[] files = Directory.GetFiles(saves, "*.prec", SearchOption.AllDirectories);
            Assert.NotEmpty(files);

            int read = 0;
            int coveredEveryPass = 0;
            var unsettled = new List<string>();
            foreach (string file in files)
            {
                if (!TrajectorySidecarBinary.TryProbe(file, out TrajectorySidecarProbe probe)
                    || !probe.Supported)
                {
                    continue;
                }
                var rec = new Recording { RecordingId = Path.GetFileNameWithoutExtension(file) };
                TrajectorySidecarBinary.Read(file, rec, probe);
                read++;

                OrbitSegmentCheckpointBridge
                    .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);
                var second = OrbitSegmentCheckpointBridge
                    .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);
                if (second.SkippedCovered > 0)
                    coveredEveryPass++;
                if (second.AnyMutation)
                    unsettled.Add(file.Substring(saves.Length));
            }

            Assert.True(read > 0, "no committed .prec could be read");
            // Not vacuous: the recorded shape is in the committed set.
            Assert.True(coveredEveryPass > 0, "no fixture holds a checkpoint wider than its section");
            Assert.True(unsettled.Count == 0,
                "unsettled after one pass: " + string.Join(", ", unsettled));
        }
    }
}
