using System;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    public class TrajectorySidecarProbeVersionContractTests
    {
        [Fact]
        public void CurrentVersionPair_IsAccepted()
        {
            Assert.True(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion,
                RecordingStore.CurrentRecordingFormatVersion));
        }

        [Fact]
        public void LegacyEqualVersionPair_IsRejected()
        {
            Assert.False(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion - 1,
                RecordingStore.CurrentRecordingFormatVersion - 1));
        }

        [Fact]
        public void MismatchedVersionPair_IsRejected()
        {
            Assert.False(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion - 1,
                RecordingStore.CurrentRecordingFormatVersion));
        }

        [Fact]
        public void TextConfigNode_CurrentVersion_IsSupported()
        {
            string path = SaveTextSidecar(RecordingStore.CurrentRecordingFormatVersion);
            try
            {
                TrajectorySidecarProbe probe;
                Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));

                Assert.True(probe.Supported);
                Assert.Equal(TrajectorySidecarEncoding.TextConfigNode, probe.Encoding);
                Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
                Assert.Null(probe.FailureReason);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TextConfigNode_PreCurrentVersion_IsUnsupported()
        {
            int legacyVersion = RecordingStore.CurrentRecordingFormatVersion - 1;
            string path = SaveTextSidecar(legacyVersion);
            try
            {
                TrajectorySidecarProbe probe;
                Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));

                Assert.False(probe.Supported);
                Assert.Equal(TrajectorySidecarEncoding.TextConfigNode, probe.Encoding);
                Assert.Equal(legacyVersion, probe.FormatVersion);
                Assert.Contains("unsupported text trajectory version", probe.FailureReason);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string SaveTextSidecar(int formatVersion)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "parsek-text-sidecar-probe-" + Guid.NewGuid().ToString("N") + ".prec");
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", formatVersion.ToString(CultureInfo.InvariantCulture));
            node.AddValue("recordingId", "probe-rec");
            node.AddValue("sidecarEpoch", "3");
            node.Save(path);
            return path;
        }
    }
}
