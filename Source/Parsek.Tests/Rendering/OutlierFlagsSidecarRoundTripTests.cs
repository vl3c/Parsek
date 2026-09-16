using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 8 round-trip tests for the <c>OutlierFlagsList</c> block in the
    /// <c>.pann</c> binary (design doc §17.3.1, §18 Phase 8). Verifies the
    /// schema declared in §17.3.1 is faithfully read and written.
    /// </summary>
    [Collection("Sequential")]
    public class OutlierFlagsSidecarRoundTripTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();

        public OutlierFlagsSidecarRoundTripTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pann_outlier_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            SmoothingPipeline.ResetForTesting();
            // One cell installs a rotation-period seam; clearing it on BOTH ends keeps it from
            // answering NaN for every body in the rest of the Sequential collection.
            TrajectoryMath.FrameTransform.ResetForTesting();
        }

        public void Dispose()
        {
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static OutlierFlags MakeFlags(int sectionIndex, int sampleCount, int rejectedEvery,
            byte mask)
        {
            bool[] perSample = new bool[sampleCount];
            int rejected = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                if ((i % rejectedEvery) == 0)
                {
                    perSample[i] = true;
                    rejected++;
                }
            }
            return new OutlierFlags
            {
                SectionIndex = sectionIndex,
                ClassifierMask = mask,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(perSample),
                RejectedCount = rejected,
                SampleCount = sampleCount,
            };
        }

        [Fact]
        public void Write_Read_RoundTrip_PreservesAllFields()
        {
            // What makes it fail: a silent mutation of any OutlierFlagsList
            // field on save/load would feed the spline a different rejection
            // set than the one detected at commit (HR-3 violation).
            string path = Path.Combine(tempDir, "rec.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);

            var entries = new List<KeyValuePair<int, OutlierFlags>>
            {
                new KeyValuePair<int, OutlierFlags>(0, MakeFlags(0, 16, rejectedEvery: 4, mask: 0x01)),
                new KeyValuePair<int, OutlierFlags>(2, MakeFlags(2, 32, rejectedEvery: 5, mask: 0x06)),
                new KeyValuePair<int, OutlierFlags>(7, MakeFlags(7, 100, rejectedEvery: 7, mask: 0x0F)),
            };

            PannotationsSidecarBinary.Write(path, "rec-out", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                outlierFlags: entries);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Supported);

            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands,
                out List<KeyValuePair<int, OutlierFlags>> readFlags,
                out string failure));
            Assert.Null(failure);
            Assert.Equal(entries.Count, readFlags.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.Equal(entries[i].Key, readFlags[i].Key);
                OutlierFlags input = entries[i].Value;
                OutlierFlags output = readFlags[i].Value;
                Assert.Equal(input.SectionIndex, output.SectionIndex);
                Assert.Equal(input.ClassifierMask, output.ClassifierMask);
                Assert.Equal(input.RejectedCount, output.RejectedCount);
                Assert.Equal(input.PackedBitmap, output.PackedBitmap);
                // SampleCount is NOT persisted; reader returns 0 and the
                // SmoothingPipeline install path backfills it from the live
                // section's frame count.
                Assert.Equal(0, output.SampleCount);
            }
        }

        [Fact]
        public void Write_Read_EmptyOutlierList_StillReadable()
        {
            // What makes it fail: the count=0 fast path was the Phase-1
            // contract; if the writer or reader can't handle an empty list,
            // the backward-compatible "no krakens detected" steady state
            // breaks.
            string path = Path.Combine(tempDir, "rec-empty.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);

            PannotationsSidecarBinary.Write(path, "rec-empty", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                outlierFlags: null);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands,
                out List<KeyValuePair<int, OutlierFlags>> readFlags,
                out string failure));
            Assert.Null(failure);
            Assert.Empty(readFlags);
        }

        [Fact]
        public void Write_Read_BitmapWithEmptyContent_RoundTrip()
        {
            // Edge case: an entry with a zero-length bitmap (e.g. a section
            // OutlierFlags.Empty placeholder serialised even though the
            // writer typically drops them — this proves the schema is
            // robust to that boundary case).
            string path = Path.Combine(tempDir, "rec-zeromap.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var entries = new List<KeyValuePair<int, OutlierFlags>>
            {
                new KeyValuePair<int, OutlierFlags>(5, new OutlierFlags
                {
                    SectionIndex = 5,
                    ClassifierMask = 0,
                    PackedBitmap = new byte[0],
                    RejectedCount = 0,
                    SampleCount = 0,
                }),
            };
            PannotationsSidecarBinary.Write(path, "rec-zm", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                outlierFlags: entries);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands,
                out List<KeyValuePair<int, OutlierFlags>> readFlags,
                out string failure));
            Assert.Null(failure);
            Assert.Single(readFlags);
            Assert.Equal(5, readFlags[0].Key);
            Assert.Empty(readFlags[0].Value.PackedBitmap);
        }

        [Fact]
        public void AlgorithmStampDrift_V6_To_V7_DiscardsOldFile()
        {
            // What makes it fail: Phase 8 bumped AlgorithmStampVersion 6 → 7.
            // A v6 .pann file (lacks populated outlier flags) MUST trigger
            // alg-stamp-drift on first load so the file is discarded and
            // recomputed (HR-10).
            string path = Path.Combine(tempDir, "rec-v6.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            // Write with current alg stamp (v7) then mutate algStamp to 6
            // — the bytes are at offset 8..11 (after Magic[0..3] + binVer[4..7]).
            PannotationsSidecarBinary.Write(path, "rec-v6", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());
            byte[] bytes = File.ReadAllBytes(path);
            bytes[8] = 6; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0;
            File.WriteAllBytes(path, bytes);

            // Build a recording matching the file's id/epoch/format.
            var rec = new Recording
            {
                RecordingId = "rec-v6",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 1,
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100,
                endUT = 109,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 80000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 101, latitude = 0.001, longitude = 0.001, altitude = 80000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 102, latitude = 0.002, longitude = 0.002, altitude = 80000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 103, latitude = 0.003, longitude = 0.003, altitude = 80000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 104, latitude = 0.004, longitude = 0.004, altitude = 80000, bodyName = "Kerbin" },
                },
                checkpoints = new List<OrbitSegment>(),
            });
            // Provide a body resolver so the inertial fit works.
            CelestialBody fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            SmoothingPipeline.BodyResolverForTesting = name => name == "Kerbin" ? fakeKerbin : null;
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, fakeKerbin) ? 21549.425 : double.NaN;

            SmoothingPipeline.LoadOrCompute(rec, path);

            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-Sidecar]")
                && l.Contains("whole-file invalidation")
                && l.Contains("reason=alg-stamp-drift"));
        }

        [Fact]
        public void Read_NegativeBitmapLength_RejectedAsCorrupt()
        {
            // What makes it fail: a missing length-validation could attempt
            // a negative-size byte[] alloc, throw, and leave the path in a
            // non-deterministic state. The reader contract returns false
            // with a populated failureReason and lets the orchestrator
            // recompute (HR-9 visible failure).
            string path = Path.Combine(tempDir, "rec-corrupt.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var entries = new List<KeyValuePair<int, OutlierFlags>>
            {
                new KeyValuePair<int, OutlierFlags>(0, new OutlierFlags
                {
                    SectionIndex = 0,
                    ClassifierMask = 0,
                    PackedBitmap = new byte[] { 0x05 },
                    RejectedCount = 2,
                    SampleCount = 0,
                }),
            };
            PannotationsSidecarBinary.Write(path, "rec-corrupt", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                outlierFlags: entries);

            // Locate the bitmap-length int32 in the file. It follows:
            //   header (Magic 4 + binVer 4 + algStamp 4 + epoch 4 + fmtVer 4 + hash 32 + recId LEB128+UTF8)
            // + string-table count int32 (0)
            // + spline-list count int32 (0)
            // + outlier-list count int32 (1)
            // + sectionIndex (4) + classifierMask (1) + bitmapLength (4) ...
            // + bitmap bytes + rejectedCount (4) + anchor-candidate count (4).
            // The trailing bytes are known exactly: bitmap = 1 byte (0x05),
            // rejectedCount = 4 and the empty anchor-candidate block's count =
            // 4, so the bitmapLength int32 sits at bytes[len-13..len-10].
            // The last int32 in the file is the ANCHOR-CANDIDATE count, not
            // rejectedCount: writing -2 there (what this cell used to do) is
            // refused by the anchor-candidate validator, several guards past
            // the one this cell names. rejectedCount is not validated at all.
            byte[] bytes = File.ReadAllBytes(path);
            int lenOffset = bytes.Length - 13;
            bytes[lenOffset] = 0xFF;
            bytes[lenOffset + 1] = 0xFF;
            bytes[lenOffset + 2] = 0xFF;
            bytes[lenOffset + 3] = 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands,
                out List<KeyValuePair<int, OutlierFlags>> flags,
                out string failure);
            Assert.False(ok);
            Assert.NotNull(failure);
            // The NEGATIVE-count validator must be what refuses the file, not
            // the later truncation check: with -1 the reader allocates an empty
            // bitmap and the length comparison would also return false, so
            // without reading the reason this cell cannot tell the two apart.
            // The field name and the "(negative)" tag are culture-free literals
            // written by PannotationsSidecarBinary.ValidateCount.
            Assert.Contains("outlier-flags[0].bitmap", failure);
            Assert.Contains("(negative)", failure);
            Assert.DoesNotContain("truncated bitmap", failure);
        }
    }
}
