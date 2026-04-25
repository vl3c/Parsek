using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Parsek
{
    internal enum SnapshotSidecarEncoding
    {
        TextConfigNode = 0,
        DeflateV1 = 1,
        UnknownBinary = 2
    }

    internal struct SnapshotSidecarProbe
    {
        public bool Success;
        public bool Supported;
        public SnapshotSidecarEncoding Encoding;
        public int FormatVersion;
        public byte Codec;
        public string NodeName;
        public int UncompressedLength;
        public int CompressedLength;
        public uint Checksum;
        public ConfigNode LegacyNode;
        public string FailureReason;
    }

    internal static class SnapshotSidecarCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PRKS");
        private static readonly uint[] Crc32Table = BuildCrc32Table();
        private const int CurrentFormatVersion = 1;
        private const byte CodecDeflate = 1;
        private const string WrapperNodeName = "SNAPSHOT_SIDECAR";
        private const string FallbackNodeName = "SNAPSHOT";
        private const int HeaderByteCount = 4 + sizeof(int) + sizeof(byte) + sizeof(int) + sizeof(int) + sizeof(uint);
        private const int MaxPayloadBytesLimit = 16 * 1024 * 1024;

        internal static int CurrentVersion => CurrentFormatVersion;
        internal static string CurrentEncodingLabel => "DeflateV1";
        internal static string CurrentCompressionLevelLabel => "Optimal";
        internal static CompressionLevel CurrentCompressionLevel => CompressionLevel.Optimal;
        internal static int MaxPayloadBytes => MaxPayloadBytesLimit;

        internal static bool HasBinaryMagic(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < Magic.Length)
                    return false;

                for (int i = 0; i < Magic.Length; i++)
                {
                    if (stream.ReadByte() != Magic[i])
                        return false;
                }
            }

            return true;
        }

        internal static bool TryProbe(string path, out SnapshotSidecarProbe probe)
        {
            probe = default(SnapshotSidecarProbe);

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                probe.FailureReason = "file missing";
                return false;
            }

            try
            {
                if (HasBinaryMagic(path))
                    return TryProbeBinary(path, out probe);

                var legacyNode = ConfigNode.Load(path);
                if (legacyNode == null)
                {
                    probe.Encoding = SnapshotSidecarEncoding.TextConfigNode;
                    probe.FailureReason = "legacy text parse failed";
                    return false;
                }

                long fileLength = new FileInfo(path).Length;
                probe = new SnapshotSidecarProbe
                {
                    Success = true,
                    Supported = true,
                    Encoding = SnapshotSidecarEncoding.TextConfigNode,
                    FormatVersion = 0,
                    Codec = 0,
                    NodeName = legacyNode.name,
                    UncompressedLength = fileLength > int.MaxValue ? int.MaxValue : (int)fileLength,
                    CompressedLength = 0,
                    Checksum = 0,
                    LegacyNode = legacyNode,
                    FailureReason = null
                };
                return true;
            }
            catch (Exception ex)
            {
                probe.FailureReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static bool TryLoad(string path, out ConfigNode node, out SnapshotSidecarProbe probe)
        {
            node = null;
            if (!TryProbe(path, out probe))
                return false;

            if (!probe.Supported)
                return false;

            if (probe.Encoding == SnapshotSidecarEncoding.TextConfigNode)
            {
                node = probe.LegacyNode;
                return node != null;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (!TryReadBinaryHeader(reader, stream, out probe))
                        return false;

                    byte[] compressedPayload = reader.ReadBytes(probe.CompressedLength);
                    if (compressedPayload.Length != probe.CompressedLength)
                    {
                        probe.FailureReason = "compressed payload truncated";
                        return false;
                    }

                    byte[] payload = Decompress(compressedPayload, probe.UncompressedLength);
                    if (payload.Length != probe.UncompressedLength)
                    {
                        probe.FailureReason = "uncompressed length mismatch";
                        return false;
                    }

                    if (ComputeCrc32(payload) != probe.Checksum)
                    {
                        probe.FailureReason = "checksum mismatch";
                        return false;
                    }

                    node = DeserializeWrappedPayload(payload, out string nodeName);
                    if (node == null)
                    {
                        probe.FailureReason = "wrapped snapshot parse failed";
                        return false;
                    }

                    probe.NodeName = nodeName;
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                probe.FailureReason = "compressed payload invalid";
                return false;
            }
            catch (Exception ex)
            {
                probe.FailureReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static void Write(string path, ConfigNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            byte[] payload = SerializeWrappedPayload(node);
            if (payload.Length > MaxPayloadBytesLimit)
                throw new InvalidOperationException($"Snapshot sidecar payload too large ({payload.Length} bytes)");

            byte[] compressedPayload = Compress(payload);
            if (compressedPayload.Length > MaxPayloadBytesLimit)
                throw new InvalidOperationException($"Compressed snapshot sidecar payload too large ({compressedPayload.Length} bytes)");

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(CurrentFormatVersion);
                writer.Write(CodecDeflate);
                writer.Write(payload.Length);
                writer.Write(compressedPayload.Length);
                writer.Write(ComputeCrc32(payload));
                writer.Write(compressedPayload);
                writer.Flush();

                FileIOUtils.SafeWriteBytes(stream.ToArray(), path, "RecordingStore");
            }
        }

        internal static string GetEncodingLabel(SnapshotSidecarProbe probe)
        {
            if (probe.Encoding == SnapshotSidecarEncoding.DeflateV1)
                return CurrentEncodingLabel;
            if (probe.Encoding == SnapshotSidecarEncoding.TextConfigNode)
                return "TextConfigNode";
            return "UnknownBinary";
        }

        internal static string DescribeProbe(SnapshotSidecarProbe probe)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "success={0} supported={1} encoding={2} version={3} codec={4} node={5} " +
                "uncompressedBytes={6} compressedBytes={7} checksum={8} failure={9}",
                probe.Success,
                probe.Supported,
                GetEncodingLabel(probe),
                probe.FormatVersion,
                probe.Codec,
                string.IsNullOrEmpty(probe.NodeName) ? "<none>" : probe.NodeName,
                probe.UncompressedLength,
                probe.CompressedLength,
                probe.Checksum,
                string.IsNullOrEmpty(probe.FailureReason) ? "<none>" : "'" + probe.FailureReason + "'");
        }

        private static bool TryProbeBinary(string path, out SnapshotSidecarProbe probe)
        {
            probe = default(SnapshotSidecarProbe);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                return TryReadBinaryHeader(reader, stream, out probe);
            }
        }

        private static bool TryReadBinaryHeader(BinaryReader reader, Stream stream, out SnapshotSidecarProbe probe)
        {
            probe = new SnapshotSidecarProbe
            {
                Encoding = SnapshotSidecarEncoding.UnknownBinary
            };

            if (stream.Length < HeaderByteCount)
            {
                probe.FailureReason = "binary header truncated";
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (reader.ReadByte() != Magic[i])
                {
                    probe.FailureReason = "binary magic mismatch";
                    return false;
                }
            }

            int formatVersion = reader.ReadInt32();
            byte codec = reader.ReadByte();
            int uncompressedLength = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();
            uint checksum = reader.ReadUInt32();

            if (uncompressedLength < 0)
            {
                probe.FailureReason = "negative uncompressed length";
                return false;
            }

            if (uncompressedLength > MaxPayloadBytesLimit)
            {
                probe.FailureReason = $"uncompressed payload too large ({uncompressedLength} bytes)";
                return false;
            }

            if (compressedLength < 0)
            {
                probe.FailureReason = "negative compressed length";
                return false;
            }

            if (compressedLength > MaxPayloadBytesLimit)
            {
                probe.FailureReason = $"compressed payload too large ({compressedLength} bytes)";
                return false;
            }

            long remainingBytes = stream.Length - stream.Position;
            if (remainingBytes != compressedLength)
            {
                probe.FailureReason = remainingBytes < compressedLength
                    ? "compressed payload truncated"
                    : "compressed length mismatch";
                return false;
            }

            bool supported = formatVersion == CurrentFormatVersion && codec == CodecDeflate;
            probe = new SnapshotSidecarProbe
            {
                Success = true,
                Supported = supported,
                Encoding = supported ? SnapshotSidecarEncoding.DeflateV1 : SnapshotSidecarEncoding.UnknownBinary,
                FormatVersion = formatVersion,
                Codec = codec,
                NodeName = null,
                UncompressedLength = uncompressedLength,
                CompressedLength = compressedLength,
                Checksum = checksum,
                LegacyNode = null,
                FailureReason = supported
                    ? null
                    : $"unsupported snapshot sidecar version {formatVersion} codec {codec}"
            };
            return true;
        }

        private static byte[] SerializeWrappedPayload(ConfigNode node)
        {
            var wrapper = new ConfigNode(WrapperNodeName);
            string nodeName = string.IsNullOrEmpty(node.name) ? FallbackNodeName : node.name;
            wrapper.AddNode(nodeName, node.CreateCopy());
            return Encoding.UTF8.GetBytes(wrapper.ToString());
        }

        private static ConfigNode DeserializeWrappedPayload(byte[] payload, out string nodeName)
        {
            nodeName = null;
            if (payload == null)
                return null;

            string serialized = Encoding.UTF8.GetString(payload);
            ConfigNode parsed = ConfigNode.Parse(serialized);
            if (parsed == null)
                return null;

            ConfigNode wrapper = parsed.GetNode(WrapperNodeName);
            if (wrapper == null || wrapper.values == null || wrapper.nodes == null)
                return null;

            if (wrapper.values.Count != 0 || wrapper.nodes.Count != 1 || wrapper.nodes[0] == null)
                return null;

            nodeName = wrapper.nodes[0].name;
            return wrapper.nodes[0].CreateCopy();
        }

        private static byte[] Compress(byte[] payload)
        {
            using (var stream = new MemoryStream())
            {
                using (var deflater = new DeflateStream(stream, CurrentCompressionLevel, leaveOpen: true))
                {
                    deflater.Write(payload, 0, payload.Length);
                }

                return stream.ToArray();
            }
        }

        private static byte[] Decompress(byte[] payload, int expectedLength)
        {
            using (var input = new MemoryStream(payload))
            using (var deflater = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = expectedLength > 0 ? new MemoryStream(expectedLength) : new MemoryStream())
            {
                deflater.CopyTo(output);
                return output.ToArray();
            }
        }

        private static uint ComputeCrc32(byte[] payload)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < payload.Length; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ payload[i]) & 0xFF];
            return ~crc;
        }

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
