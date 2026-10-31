using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level round-trip of the <c>.pann</c> annotation sidecar. The file is a
    /// regenerable cache (a header mismatch discards and recomputes it), but a file whose
    /// header matches is installed as-is, so every field of its payload must come back:
    /// <see cref="SmoothingSpline"/>, <see cref="OutlierFlags"/> and
    /// <see cref="AnchorCandidate"/> per section, and the cache-key header the probe reads.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(PannotationsSidecarBinary))]
    public class PannotationsSidecarSaveRoundTripTests : IDisposable
    {
        private readonly string tempDir;

        public PannotationsSidecarSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-rt-pann-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { }
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        internal static FieldSpec[] Specs() => new[]
        {
            new FieldSpec(typeof(SmoothingSpline))
                .Derived("recomputed on read as knotCount > 0; the writer only receives valid splines",
                    "IsValid"),
            new FieldSpec(typeof(OutlierFlags))
                .NotPersisted("the section's sample count; SmoothingPipeline.LoadOrCompute refills it "
                    + "from the live section's frames after the read", "SampleCount"),
        };

        private sealed class Payload
        {
            internal List<KeyValuePair<int, SmoothingSpline>> Splines = new List<KeyValuePair<int, SmoothingSpline>>();
            internal List<KeyValuePair<int, AnchorCandidate[]>> Anchors = new List<KeyValuePair<int, AnchorCandidate[]>>();
            internal List<KeyValuePair<int, OutlierFlags>> Outliers = new List<KeyValuePair<int, OutlierFlags>>();
        }

        private static Payload BuildPayload(FieldRoundTrip rt)
        {
            var p = new Payload();
            for (int i = 0; i < 3; i++)
            {
                p.Splines.Add(new KeyValuePair<int, SmoothingSpline>(i * 2, rt.Build<SmoothingSpline>()));

                // The writer stores the list key as the section index and the reader puts it
                // back into OutlierFlags.SectionIndex, so a producer-shaped entry keys on it.
                OutlierFlags flags = rt.Build<OutlierFlags>();
                p.Outliers.Add(new KeyValuePair<int, OutlierFlags>(flags.SectionIndex, flags));

                p.Anchors.Add(new KeyValuePair<int, AnchorCandidate[]>(i * 3 + 1, new[]
                {
                    rt.Build<AnchorCandidate>(),
                    new AnchorCandidate(500.5 + i, AnchorSource.LiveSeparation, AnchorSide.Start),
                    new AnchorCandidate(600.5 + i, AnchorSource.Loop, AnchorSide.End),
                }));
            }
            return p;
        }

        [Fact]
        public void Pannotations_EveryPayloadAndHeaderFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Specs(), seed: 100);
                Payload written = BuildPayload(rt);
                byte[] hash = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();
                string path = Path.Combine(tempDir, "rt.pann");

                PannotationsSidecarBinary.Write(path, "rt-recording", 41, 3, hash,
                    written.Splines, written.Anchors, written.Outliers);

                Assert.True(PannotationsSidecarBinary.TryProbe(path, out PannotationsSidecarProbe probe), probe.FailureReason);
                Assert.True(probe.Supported, probe.FailureReason);
                Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                    out List<KeyValuePair<int, SmoothingSpline>> splines,
                    out List<KeyValuePair<int, AnchorCandidate[]>> anchors,
                    out List<KeyValuePair<int, OutlierFlags>> outliers,
                    out string failure), failure);

                var problems = new List<string>();
                problems.AddRange(rt.Diff(written.Splines, splines, "Splines"));
                problems.AddRange(rt.Diff(written.Anchors, anchors, "AnchorCandidates"));
                problems.AddRange(rt.Diff(written.Outliers, outliers, "OutlierFlags"));
                Assert.True(problems.Count == 0, FieldRoundTrip.Report(".pann payload", problems));

                Assert.Equal("rt-recording", probe.RecordingId);
                Assert.Equal(41, probe.SourceSidecarEpoch);
                Assert.Equal(3, probe.SourceRecordingFormatVersion);
                Assert.Equal(hash, probe.ConfigurationHash);
                Assert.Equal(PannotationsSidecarBinary.PannotationsBinaryVersion, probe.BinaryVersion);
                Assert.Equal(PannotationsSidecarBinary.AlgorithmStampVersion, probe.AlgorithmStampVersion);
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            // Without its NotPersisted classification, OutlierFlags.SampleCount (which the
            // codec does not write) must be reported.
            var rt = new FieldRoundTrip(new FieldSpec[0], seed: 200);
            OutlierFlags flags = rt.Build<OutlierFlags>();
            string path = Path.Combine(tempDir, "negative.pann");
            PannotationsSidecarBinary.Write(path, "neg", 1, 1, new byte[32],
                new List<KeyValuePair<int, SmoothingSpline>>(),
                null,
                new List<KeyValuePair<int, OutlierFlags>> { new KeyValuePair<int, OutlierFlags>(flags.SectionIndex, flags) });
            Assert.True(PannotationsSidecarBinary.TryProbe(path, out PannotationsSidecarProbe probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe, out _, out _,
                out List<KeyValuePair<int, OutlierFlags>> outliers, out string failure), failure);

            List<string> problems = rt.Diff(flags, outliers.Single().Value, "OutlierFlags");
            Assert.Contains(problems, p => p.Contains("SampleCount"));
        }
    }
}
