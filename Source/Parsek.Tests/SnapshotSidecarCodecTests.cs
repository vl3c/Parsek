using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SnapshotSidecarCodecTests : IDisposable
    {
        private const byte CodecDeflate = 1;
        private const string WrapperNodeName = "SNAPSHOT_SIDECAR";
        private const string FallbackNodeName = "SNAPSHOT";
        private readonly string tempDir;

        public SnapshotSidecarCodecTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-snapshot-sidecar-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void Write_Load_UnnamedNodeFallsBackToSnapshotNodeName()
        {
            var original = new ConfigNode(string.Empty);
            original.AddValue("name", "Unnamed Snapshot");
            var part = original.AddNode("PART");
            part.AddValue("name", "probeCoreOcto");
            part.AddValue("persistentId", "42");

            string path = Path.Combine(tempDir, "fallback-node-name.craft");
            SnapshotSidecarCodec.Write(path, original);

            ConfigNode loaded;
            SnapshotSidecarProbe probe;
            Assert.True(SnapshotSidecarCodec.TryLoad(path, out loaded, out probe));
            Assert.NotNull(loaded);
            Assert.Equal(SnapshotSidecarEncoding.DeflateV1, probe.Encoding);
            Assert.Equal(FallbackNodeName, probe.NodeName);
            Assert.Equal(FallbackNodeName, loaded.name);

            var expected = original.CreateCopy();
            expected.name = FallbackNodeName;
            AssertConfigNodeEquivalent(expected, loaded);
        }

        [Fact]
        public void TryProbe_UnsupportedCodec_ReportsUnknownBinaryMetadata()
        {
            ConfigNode payloadNode = VesselSnapshotBuilder.ProbeShip("Unsupported Codec", pid: 2001).Build();
            string path = Path.Combine(tempDir, "unsupported-codec.craft");
            WriteBinarySnapshotEnvelope(
                path,
                SerializeWrappedPayload(payloadNode),
                SnapshotSidecarCodec.CurrentVersion,
                codec: 7);

            SnapshotSidecarProbe probe;
            Assert.True(SnapshotSidecarCodec.TryProbe(path, out probe));
            Assert.True(probe.Success);
            Assert.False(probe.Supported);
            Assert.Equal(SnapshotSidecarEncoding.UnknownBinary, probe.Encoding);
            Assert.Equal(7, probe.Codec);
            Assert.Equal(SnapshotSidecarCodec.CurrentVersion, probe.FormatVersion);
            Assert.Equal(
                $"unsupported snapshot sidecar version {SnapshotSidecarCodec.CurrentVersion} codec 7",
                probe.FailureReason);
        }

        [Fact]
        public void TryLoad_ChecksumMismatch_ReturnsFalseAfterSuccessfulHeaderProbe()
        {
            ConfigNode original = VesselSnapshotBuilder.FleaRocket(
                "Checksum Mismatch",
                "Jebediah Kerman",
                pid: 3001).Build();
            string path = Path.Combine(tempDir, "checksum-mismatch.craft");
            SnapshotSidecarCodec.Write(path, original);

            byte[] bytes = File.ReadAllBytes(path);
            int checksumOffset = 4 + sizeof(int) + sizeof(byte) + sizeof(int) + sizeof(int);
            bytes[checksumOffset] ^= 0x5A;
            File.WriteAllBytes(path, bytes);

            ConfigNode loaded;
            SnapshotSidecarProbe probe;
            Assert.False(SnapshotSidecarCodec.TryLoad(path, out loaded, out probe));
            Assert.Null(loaded);
            Assert.True(probe.Success);
            Assert.Equal("checksum mismatch", probe.FailureReason);
        }

        [Fact]
        public void TryLoad_InvalidWrapperShape_ReturnsWrappedSnapshotParseFailed()
        {
            var wrapper = new ConfigNode(WrapperNodeName);
            wrapper.AddValue("unexpected", "value");
            var inner = wrapper.AddNode("VESSEL");
            inner.AddValue("name", "Bad Wrapper");
            inner.AddValue("persistentId", "4001");

            string path = Path.Combine(tempDir, "invalid-wrapper-shape.craft");
            WriteBinarySnapshotEnvelope(
                path,
                Encoding.UTF8.GetBytes(wrapper.ToString()),
                SnapshotSidecarCodec.CurrentVersion,
                CodecDeflate);

            ConfigNode loaded;
            SnapshotSidecarProbe probe;
            Assert.False(SnapshotSidecarCodec.TryLoad(path, out loaded, out probe));
            Assert.Null(loaded);
            Assert.True(probe.Success);
            Assert.Equal("wrapped snapshot parse failed", probe.FailureReason);
        }

        private static byte[] SerializeWrappedPayload(ConfigNode node)
        {
            var wrapper = new ConfigNode(WrapperNodeName);
            string nodeName = string.IsNullOrEmpty(node?.name) ? FallbackNodeName : node.name;
            wrapper.AddNode(nodeName, node.CreateCopy());
            return Encoding.UTF8.GetBytes(wrapper.ToString());
        }

        private static void WriteBinarySnapshotEnvelope(string path, byte[] payload, int version, byte codec)
        {
            byte[] compressedPayload = Compress(payload);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes("PRKS"));
                writer.Write(version);
                writer.Write(codec);
                writer.Write(payload.Length);
                writer.Write(compressedPayload.Length);
                writer.Write(ComputeCrc32(payload));
                writer.Write(compressedPayload);
            }
        }

        private static byte[] Compress(byte[] payload)
        {
            using (var stream = new MemoryStream())
            {
                using (var deflater = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflater.Write(payload, 0, payload.Length);
                }

                return stream.ToArray();
            }
        }

        private static uint ComputeCrc32(byte[] payload)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < payload.Length; i++)
            {
                crc = (crc >> 8) ^ Crc32Table[(crc ^ payload[i]) & 0xFF];
            }

            return ~crc;
        }

        private static void AssertConfigNodeEquivalent(ConfigNode expected, ConfigNode actual)
        {
            Assert.NotNull(expected);
            Assert.NotNull(actual);

            actual = NormalizeLoadedNode(expected.name, actual);
            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.values.Count, actual.values.Count);
            for (int i = 0; i < expected.values.Count; i++)
            {
                Assert.Equal(expected.values[i].name, actual.values[i].name);
                Assert.Equal(expected.values[i].value, actual.values[i].value);
            }

            Assert.Equal(expected.nodes.Count, actual.nodes.Count);
            for (int i = 0; i < expected.nodes.Count; i++)
                AssertConfigNodeEquivalent(expected.nodes[i], actual.nodes[i]);
        }

        private static ConfigNode NormalizeLoadedNode(string expectedName, ConfigNode node)
        {
            if (node == null)
                return null;

            if (node.name == expectedName)
                return node;

            if (node.name == "root" &&
                node.nodes != null &&
                node.nodes.Count == 1 &&
                node.nodes[0].name == expectedName)
            {
                return node.nodes[0];
            }

            return node;
        }

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((value & 1u) != 0)
                        value = 0xEDB88320u ^ (value >> 1);
                    else
                        value >>= 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
