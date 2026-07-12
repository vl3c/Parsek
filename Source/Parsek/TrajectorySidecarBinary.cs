using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Parsek
{
    internal enum TrajectorySidecarEncoding
    {
        TextConfigNode = 0,
        BinaryV0 = 1,
        UnknownBinary = 2
    }

    internal struct TrajectorySidecarProbe
    {
        public bool Success;
        public bool Supported;
        public TrajectorySidecarEncoding Encoding;
        public int FormatVersion;
        public int SchemaGeneration;
        public int SidecarEpoch;
        public string RecordingId;
        public string MagicTag;
        public ConfigNode LegacyNode;
        public string FailureReason;
    }

    internal static class TrajectorySidecarBinary
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PSK0");
        private static readonly byte[] PreResetMagic = Encoding.ASCII.GetBytes("PRKB");
        internal const int CurrentBinaryVersion = RecordingStore.CurrentRecordingFormatVersion;
        private const byte FlagSectionAuthoritative = 1 << 0;
        private const byte OrbitSegmentFlagPredicted = 1 << 0;
        private const byte SparsePointListFlagEnabled = 1 << 0;
        private const byte SparsePointListFlagBodyDefault = 1 << 1;
        private const byte SparsePointListFlagFundsDefault = 1 << 2;
        private const byte SparsePointListFlagScienceDefault = 1 << 3;
        private const byte SparsePointListFlagReputationDefault = 1 << 4;
        private const byte SparsePointOverrideBody = 1 << 0;
        private const byte SparsePointOverrideFunds = 1 << 1;
        private const byte SparsePointOverrideScience = 1 << 2;
        private const byte SparsePointOverrideReputation = 1 << 3;

        internal static bool HasBinaryMagic(string path)
        {
            return HasMagic(path, Magic);
        }

        internal static bool HasPreResetBinaryMagic(string path)
        {
            return HasMagic(path, PreResetMagic);
        }

        private static bool HasMagic(string path, byte[] magic)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < magic.Length)
                    return false;

                for (int i = 0; i < magic.Length; i++)
                {
                    int b = stream.ReadByte();
                    if (b != magic[i])
                        return false;
                }
            }

            return true;
        }

        internal static TrajectorySidecarProbe BuildPreResetMagicProbe()
        {
            return new TrajectorySidecarProbe
            {
                Success = true,
                Supported = false,
                Encoding = TrajectorySidecarEncoding.UnknownBinary,
                FormatVersion = -1,
                SchemaGeneration = 0,
                SidecarEpoch = 0,
                MagicTag = Encoding.ASCII.GetString(PreResetMagic),
                FailureReason = "magic-mismatch"
            };
        }

        internal static bool TryProbe(string path, out TrajectorySidecarProbe probe)
        {
            probe = default(TrajectorySidecarProbe);

            if (!File.Exists(path))
            {
                probe.FailureReason = "file missing";
                return false;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (stream.Length < Magic.Length + sizeof(int) + sizeof(int) + sizeof(int))
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
                    int schemaGeneration = reader.ReadInt32();
                    int sidecarEpoch = reader.ReadInt32();
                    string recordingId = reader.ReadString();

                    probe.Success = true;
                    probe.Encoding = GetBinaryEncoding(formatVersion);
                    probe.FormatVersion = formatVersion;
                    probe.SchemaGeneration = schemaGeneration;
                    probe.SidecarEpoch = sidecarEpoch;
                    probe.RecordingId = recordingId;
                    probe.MagicTag = Encoding.ASCII.GetString(Magic);
                    probe.Supported = IsSupportedBinaryVersion(formatVersion)
                        && schemaGeneration == RecordingStore.CurrentRecordingSchemaGeneration;
                    probe.FailureReason = BuildUnsupportedReason(formatVersion, schemaGeneration);
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                probe.FailureReason = "binary header truncated";
                return false;
            }
            catch (FormatException ex)
            {
                probe.FailureReason = "binary header invalid: " + ex.Message;
                return false;
            }
            catch (ArgumentException ex)
            {
                probe.FailureReason = "binary header invalid: " + ex.Message;
                return false;
            }
            catch (IOException ex)
            {
                probe.FailureReason = "binary header invalid: " + ex.Message;
                return false;
            }
        }

        // Full-payload validation. The header probe (TryProbe) above stops
        // after reading magic+version+epoch+recordingId; a .prec truncated
        // past the header still satisfies that. Run the full Read into a
        // throwaway Recording and surface any reader throw as a structured
        // failure reason. The binary trajectory codec has no payload
        // checksum, so the only signal that the payload is corrupt is the
        // reader throwing — and the reader has several throw sites beyond
        // the obvious EndOfStream / Format / Argument / IO families: the
        // string-table indexers throw InvalidDataException for an
        // out-of-range index (this file, around the ReadIndexedString /
        // ReadNullableIndexedString helpers), and a garbage int32 read as
        // a list count can trigger OutOfMemoryException /
        // ArgumentOutOfRangeException when the deserializer tries to
        // preallocate. Catch broadly so any reader-thrown exception
        // becomes payload-invalid instead of aborting OnSave at
        // EnsureRecordingFilesCurrentForSave -> AreRecordingFilesCurrentForSave.
        // A mid-payload bit flip that happens to parse still slips through
        // — accepted limitation; the header probe plus the post-rewrite
        // second pass keep the contract closed-form for the common
        // truncation / missing-payload case.
        internal static bool TryValidatePayload(
            string path, TrajectorySidecarProbe probe, out string failureReason)
        {
            failureReason = null;
            if (!probe.Success || !probe.Supported)
            {
                failureReason = "probe-not-supported";
                return false;
            }
            try
            {
                Recording scratch = new Recording();
                Read(path, scratch, probe);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ClassifyPayloadException(ex) + ": " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string ClassifyPayloadException(Exception ex)
        {
            switch (ex)
            {
                case EndOfStreamException _:
                    return "payload-truncated";
                case InvalidDataException _:
                    return "payload-data-invalid";
                case FormatException _:
                    return "payload-format-invalid";
                case OverflowException _:
                    return "payload-length-overflow";
                case OutOfMemoryException _:
                    return "payload-allocation-failed";
                case ArgumentException _:
                    return "payload-argument-invalid";
                case IOException _:
                    return "payload-io-error";
                default:
                    return "payload-corrupt";
            }
        }

        internal static void Write(string path, Recording rec, int sidecarEpoch)
        {
            if (rec == null)
                throw new ArgumentNullException(nameof(rec));

            RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec,
                markDirty: false,
                context: "TrajectorySidecarBinary.Write");
            bool sectionAuthoritative = RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(rec);
            List<TrajectoryPoint> flatFallbackPoints = sectionAuthoritative
                ? null
                : TrajectoryTextSidecarCodec.GetFlatFallbackPointsForWrite(rec);
            bool wroteSafeRelativeFlatFallback =
                flatFallbackPoints != null && !ReferenceEquals(flatFallbackPoints, rec.Points);
            int flatFallbackPointCount = flatFallbackPoints != null ? flatFallbackPoints.Count : 0;
            var table = BuildStringTable(rec);
            int binaryVersion = CurrentBinaryVersion;
            SparsePointWriteStats stats = default(SparsePointWriteStats);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(binaryVersion);
                writer.Write(RecordingStore.CurrentRecordingSchemaGeneration);
                writer.Write(sidecarEpoch);
                writer.Write(rec.RecordingId ?? string.Empty);
                writer.Write(sectionAuthoritative ? FlagSectionAuthoritative : (byte)0);

                writer.Write(table.Strings.Count);
                for (int i = 0; i < table.Strings.Count; i++)
                    writer.Write(table.Strings[i] ?? string.Empty);

                WritePointList(writer, flatFallbackPoints, table, binaryVersion, ref stats);
                WriteOrbitSegmentList(writer, sectionAuthoritative ? null : rec.OrbitSegments, table, binaryVersion);
                WritePartEventList(writer, rec.PartEvents, table);
                WriteFlagEventList(writer, rec.FlagEvents, table);
                WriteSegmentEventList(writer, rec.SegmentEvents, table);
                WriteTrackSections(writer, rec.TrackSections, table, binaryVersion, ref stats);
                writer.Flush();

                FileIOUtils.SafeWriteBytes(stream.ToArray(), path, "RecordingStore");
            }

            if (!RecordingStore.SuppressLogging)
            {
                int nonDefaultSectionSources = CountNonDefaultSectionSources(rec.TrackSections);
                int predictedCheckpointCount;
                int predictedOrbitSegmentCount = RecordingStore.CountPredictedOrbitSegments(
                    rec, out predictedCheckpointCount);
                ParsekLog.Verbose("RecordingStore",
                    $"WriteBinaryTrajectoryFile: recording={rec.RecordingId} version={binaryVersion} " +
                    $"sectionAuthoritative={sectionAuthoritative} strings={table.Strings.Count} " +
                    $"points={(sectionAuthoritative ? 0 : flatFallbackPointCount)} originalPoints={rec.Points.Count} " +
                    $"safeRelativeFlatFallback={wroteSafeRelativeFlatFallback} " +
                    $"orbitSegments={(sectionAuthoritative ? 0 : rec.OrbitSegments.Count)} " +
                    $"predictedOrbitSegments={predictedOrbitSegmentCount} predictedCheckpoints={predictedCheckpointCount} " +
                    $"trackSections={rec.TrackSections?.Count ?? 0} nonDefaultSectionSources={nonDefaultSectionSources} " +
                    $"sparsePointLists={stats.SparsePointLists} sparsePoints={stats.SparsePoints} " +
                    $"omittedBody={stats.OmittedBody} omittedFunds={stats.OmittedFunds} " +
                    $"omittedScience={stats.OmittedScience} omittedRep={stats.OmittedReputation}");
            }
        }

        internal static void Read(string path, Recording rec, TrajectorySidecarProbe probe)
        {
            if (rec == null)
                throw new ArgumentNullException(nameof(rec));
            if (!probe.Success || !probe.Supported)
                throw new InvalidOperationException("Binary trajectory file probe must succeed before read.");

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                SkipHeader(reader);

                bool sectionAuthoritative = (reader.ReadByte() & FlagSectionAuthoritative) != 0;
                List<string> stringTable = ReadStringTable(reader);

                rec.Points.Clear();
                rec.OrbitSegments.Clear();
                rec.PartEvents.Clear();
                rec.FlagEvents.Clear();
                rec.SegmentEvents.Clear();
                rec.TrackSections.Clear();

                SparsePointReadStats stats = default(SparsePointReadStats);
                ReadPointList(reader, rec.Points, stringTable, probe.FormatVersion, ref stats);
                ReadOrbitSegmentList(reader, rec.OrbitSegments, stringTable, probe.FormatVersion);
                ReadPartEventList(reader, rec.PartEvents, stringTable);
                ReadFlagEventList(reader, rec.FlagEvents, stringTable);
                ReadSegmentEventList(reader, rec.SegmentEvents, stringTable);
                ReadTrackSections(reader, rec.TrackSections, stringTable, probe.FormatVersion, ref stats);
                int preHealPointCount = rec.Points.Count;
                int preHealOrbitSegmentCount = rec.OrbitSegments.Count;
                bool healedMalformedFlatFallback = false;
                if (!sectionAuthoritative &&
                    rec.TrackSections.Count > 0)
                {
                    // reconcileEmptySections: false — READ path: loading must not mutate
                    // the EXISTING sections of a committed recording. Candidate clipping
                    // still applies (it only constrains what promotion ADDS). The overall
                    // contract is normalize-on-rewrite, not byte-freeze: when a sanctioned
                    // flow (re-fly merge, tail finalizer, this dirty-marking legacy seam,
                    // ...) marks the recording dirty, the next save rewrites the sidecar
                    // through the write-path Ensure, which reconciles under the current
                    // producer contract. Files no flow dirties stay byte-identical.
                    RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                        rec,
                        markDirty: true,
                        context: "TrajectorySidecarBinary.Read",
                        reconcileEmptySections: false);
                    healedMalformedFlatFallback = RecordingStore.TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                        rec, allowRelativeSections: true);
                }

                rec.RecordingFormatVersion = probe.FormatVersion;
                rec.RecordingSchemaGeneration = probe.SchemaGeneration;
                if (string.IsNullOrEmpty(rec.RecordingId))
                    rec.RecordingId = probe.RecordingId;

                if (sectionAuthoritative)
                {
                    int dedupedPointCopies = RecordingStore.RebuildPointsFromTrackSections(rec.TrackSections, rec.Points);
                    int dedupedOrbitCopies = RecordingStore.RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rec.OrbitSegments);

                    if (!RecordingStore.SuppressLogging)
                    {
                        ParsekLog.Verbose("RecordingStore",
                            $"ReadBinaryTrajectoryFile: recording={rec.RecordingId} version={probe.FormatVersion} " +
                            $"using section-authoritative path sections={rec.TrackSections.Count} rebuiltPoints={rec.Points.Count} " +
                            $"dedupedPointCopies={dedupedPointCopies} rebuiltOrbitSegments={rec.OrbitSegments.Count} " +
                            $"dedupedOrbitCopies={dedupedOrbitCopies} sparsePointLists={stats.SparsePointLists} " +
                            $"defaultedBody={stats.DefaultedBody} defaultedFunds={stats.DefaultedFunds} " +
                            $"defaultedScience={stats.DefaultedScience} defaultedRep={stats.DefaultedReputation}");
                    }
                }
                else if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"ReadBinaryTrajectoryFile: recording={rec.RecordingId} version={probe.FormatVersion} " +
                        $"used flat fallback path healed={(healedMalformedFlatFallback ? "true" : "false")} " +
                        $"prePoints={preHealPointCount} postPoints={rec.Points.Count} " +
                        $"preOrbitSegments={preHealOrbitSegmentCount} postOrbitSegments={rec.OrbitSegments.Count} " +
                        $"trackSections={rec.TrackSections.Count} sparsePointLists={stats.SparsePointLists} " +
                        $"defaultedBody={stats.DefaultedBody} defaultedFunds={stats.DefaultedFunds} " +
                        $"defaultedScience={stats.DefaultedScience} defaultedRep={stats.DefaultedReputation}");
                }

                // #419-class load-time invariant: drop any non-monotonic flat point
                // (e.g. a sub-1s stale-UT seam from a recording made before the
                // foreground recorder gained its monotonicity guard) so it loads
                // clean instead of tripping CommittedRecordingsHaveValidData.
                int droppedNonMonotonic = RecordingStore.DropNonMonotonicTrajectoryPoints(rec.Points);
                if (droppedNonMonotonic > 0)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"ReadBinaryTrajectoryFile: recording={rec.RecordingId} dropped {droppedNonMonotonic} " +
                        "non-monotonic flat trajectory point(s) on load (#419-class)");
                }
            }
        }

        private static void SkipHeader(BinaryReader reader)
        {
            for (int i = 0; i < Magic.Length; i++)
                reader.ReadByte();

            reader.ReadInt32(); // formatVersion
            reader.ReadInt32(); // recordingSchemaGeneration
            reader.ReadInt32(); // sidecarEpoch
            reader.ReadString(); // recordingId
        }

        private static BinaryStringTable BuildStringTable(Recording rec)
        {
            var table = new BinaryStringTable();

            if (rec.Points != null)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                    table.Register(rec.Points[i].bodyName);
            }

            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                    table.Register(rec.OrbitSegments[i].bodyName);
            }

            if (rec.PartEvents != null)
            {
                for (int i = 0; i < rec.PartEvents.Count; i++)
                    table.Register(rec.PartEvents[i].partName);
            }

            if (rec.FlagEvents != null)
            {
                for (int i = 0; i < rec.FlagEvents.Count; i++)
                {
                    table.Register(rec.FlagEvents[i].flagSiteName);
                    table.Register(rec.FlagEvents[i].placedBy);
                    table.Register(rec.FlagEvents[i].plaqueText);
                    table.Register(rec.FlagEvents[i].flagURL);
                    table.Register(rec.FlagEvents[i].bodyName);
                }
            }

            if (rec.SegmentEvents != null)
            {
                for (int i = 0; i < rec.SegmentEvents.Count; i++)
                    table.Register(rec.SegmentEvents[i].details);
            }

            if (rec.TrackSections != null)
            {
                for (int t = 0; t < rec.TrackSections.Count; t++)
                {
                    var section = rec.TrackSections[t];
                    if (section.frames != null)
                    {
                        for (int i = 0; i < section.frames.Count; i++)
                            table.Register(section.frames[i].bodyName);
                    }
                    if (!string.IsNullOrEmpty(section.anchorRecordingId))
                    {
                        table.Register(section.anchorRecordingId);
                    }
                    if (section.bodyFixedFrames != null)
                    {
                        for (int i = 0; i < section.bodyFixedFrames.Count; i++)
                            table.Register(section.bodyFixedFrames[i].bodyName);
                    }

                    if (section.checkpoints != null)
                    {
                        for (int i = 0; i < section.checkpoints.Count; i++)
                            table.Register(section.checkpoints[i].bodyName);
                    }
                }
            }

            return table;
        }

        private static bool IsSupportedBinaryVersion(int version)
        {
            return version == CurrentBinaryVersion;
        }

        private static TrajectorySidecarEncoding GetBinaryEncoding(int version)
        {
            return version == CurrentBinaryVersion
                ? TrajectorySidecarEncoding.BinaryV0
                : TrajectorySidecarEncoding.UnknownBinary;
        }

        private static string BuildUnsupportedReason(int formatVersion, int schemaGeneration)
        {
            if (schemaGeneration == 0)
                return "generation-missing";
            if (schemaGeneration < RecordingStore.CurrentRecordingSchemaGeneration)
                return "generation-older";
            if (schemaGeneration > RecordingStore.CurrentRecordingSchemaGeneration)
                return "generation-newer";
            if (!IsSupportedBinaryVersion(formatVersion))
                return "format-version-mismatch";
            return null;
        }

        private static void WritePointList(BinaryWriter writer, List<TrajectoryPoint> points, BinaryStringTable table, int binaryVersion, ref SparsePointWriteStats stats)
        {
            writer.Write(points?.Count ?? 0);
            if (points == null || points.Count == 0)
                return;

            WriteSparsePointList(writer, points, table, binaryVersion, ref stats);
        }

        // Validate a list count read from the (untrusted) sidecar against
        // the remaining bytes in the stream before any per-element loop
        // or List<T> capacity allocation. A corrupted int32 that survived
        // BinaryReader.ReadInt32 still represents a count, and every list
        // element must consume at least one byte of input -- so any
        // `count` greater than `remaining bytes` is necessarily invalid.
        // Catching the OutOfMemoryException downstream of
        // `new List<T>(count)` only works because .NET Framework rejects
        // 0x7FFFFFFF immediately at the array-dimension cap; a corrupt
        // count that is large-but-allocatable (e.g. 50M) would succeed
        // the allocation, consume hundreds of MB, and only fail far
        // later when the per-element reads run out of file. Reject up
        // front instead. The minBytesPerElement argument is the
        // pessimistic lower bound on bytes-per-entry for the specific
        // list shape; pass 1 when the per-element minimum is genuinely
        // a single byte (zero-length string in the string table).
        private static int ReadBoundedCount(
            BinaryReader reader, int minBytesPerElement, string label)
        {
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException(
                    label + " count negative: " + count);
            if (count == 0)
                return 0;
            int safeMinBytes = minBytesPerElement < 1 ? 1 : minBytesPerElement;
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            long maxPossible = remaining / safeMinBytes;
            if (count > maxPossible)
                throw new InvalidDataException(
                    label + " count " + count + " exceeds remaining-bytes-based bound " +
                    maxPossible + " (remaining=" + remaining +
                    ", minBytesPerElement=" + safeMinBytes + ")");
            return count;
        }

        private static void ReadPointList(BinaryReader reader, List<TrajectoryPoint> points, List<string> stringTable, int binaryVersion, ref SparsePointReadStats stats)
        {
            // Pessimistic per-point lower bound: every point payload
            // consumes at least one byte. WritePoint actually emits ~120
            // bytes per dense point (16 doubles/floats + 1 indexed string
            // int), but sparse paths can write fewer per-element bytes
            // with shared defaults, so stay conservative at 1.
            int count = ReadBoundedCount(reader, 1, "point-list");
            if (count == 0)
                return;

            ReadSparsePointList(reader, points, stringTable, count, binaryVersion, ref stats);
        }

        private static void WritePoint(BinaryWriter writer, TrajectoryPoint pt, BinaryStringTable table, int binaryVersion)
        {
            writer.Write(pt.ut);
            writer.Write(pt.latitude);
            writer.Write(pt.longitude);
            writer.Write(pt.altitude);
            writer.Write(pt.rotation.x);
            writer.Write(pt.rotation.y);
            writer.Write(pt.rotation.z);
            writer.Write(pt.rotation.w);
            writer.Write(table.GetIndex(pt.bodyName));
            writer.Write(pt.velocity.x);
            writer.Write(pt.velocity.y);
            writer.Write(pt.velocity.z);
            writer.Write(pt.funds);
            writer.Write(pt.science);
            writer.Write(pt.reputation);
            writer.Write(pt.recordedGroundClearance);
            writer.Write(pt.flags);
        }

        private static TrajectoryPoint ReadPoint(BinaryReader reader, List<string> stringTable, int binaryVersion)
        {
            var pt = new TrajectoryPoint
            {
                ut = reader.ReadDouble(),
                latitude = reader.ReadDouble(),
                longitude = reader.ReadDouble(),
                altitude = reader.ReadDouble(),
                rotation = new Quaternion(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                bodyName = ReadIndexedString(reader, stringTable) ?? "Kerbin",
                velocity = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                funds = reader.ReadDouble(),
                science = reader.ReadSingle(),
                reputation = reader.ReadSingle()
            };
            pt.recordedGroundClearance = reader.ReadDouble();
            pt.flags = reader.ReadByte();
            return pt;
        }

        private static void WriteOrbitSegmentList(BinaryWriter writer, List<OrbitSegment> segments, BinaryStringTable table, int binaryVersion)
        {
            writer.Write(segments?.Count ?? 0);
            if (segments == null)
                return;

            for (int i = 0; i < segments.Count; i++)
                WriteOrbitSegment(writer, segments[i], table, binaryVersion);
        }

        private static void ReadOrbitSegmentList(BinaryReader reader, List<OrbitSegment> segments, List<string> stringTable, int binaryVersion)
        {
            int count = ReadBoundedCount(reader, 1, "orbit-segment-list");
            for (int i = 0; i < count; i++)
                segments.Add(ReadOrbitSegment(reader, stringTable, binaryVersion));
        }

        private static void WriteOrbitSegment(BinaryWriter writer, OrbitSegment seg, BinaryStringTable table, int binaryVersion)
        {
            writer.Write(seg.startUT);
            writer.Write(seg.endUT);
            writer.Write(seg.inclination);
            writer.Write(seg.eccentricity);
            writer.Write(seg.semiMajorAxis);
            writer.Write(seg.longitudeOfAscendingNode);
            writer.Write(seg.argumentOfPeriapsis);
            writer.Write(seg.meanAnomalyAtEpoch);
            writer.Write(seg.epoch);
            writer.Write(table.GetIndex(seg.bodyName));
            writer.Write(seg.isPredicted ? OrbitSegmentFlagPredicted : (byte)0);
            writer.Write(seg.orbitalFrameRotation.x);
            writer.Write(seg.orbitalFrameRotation.y);
            writer.Write(seg.orbitalFrameRotation.z);
            writer.Write(seg.orbitalFrameRotation.w);
            writer.Write(seg.angularVelocity.x);
            writer.Write(seg.angularVelocity.y);
            writer.Write(seg.angularVelocity.z);
        }

        private static OrbitSegment ReadOrbitSegment(BinaryReader reader, List<string> stringTable, int binaryVersion)
        {
            bool isPredicted = false;
            double startUT = reader.ReadDouble();
            double endUT = reader.ReadDouble();
            double inclination = reader.ReadDouble();
            double eccentricity = reader.ReadDouble();
            double semiMajorAxis = reader.ReadDouble();
            double longitudeOfAscendingNode = reader.ReadDouble();
            double argumentOfPeriapsis = reader.ReadDouble();
            double meanAnomalyAtEpoch = reader.ReadDouble();
            double epoch = reader.ReadDouble();
            string bodyName = ReadIndexedString(reader, stringTable) ?? "Kerbin";
            isPredicted = (reader.ReadByte() & OrbitSegmentFlagPredicted) != 0;

            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = inclination,
                eccentricity = eccentricity,
                semiMajorAxis = semiMajorAxis,
                longitudeOfAscendingNode = longitudeOfAscendingNode,
                argumentOfPeriapsis = argumentOfPeriapsis,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                bodyName = bodyName,
                isPredicted = isPredicted,
                orbitalFrameRotation = new Quaternion(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                angularVelocity = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle())
            };
        }

        private static void WritePartEventList(BinaryWriter writer, List<PartEvent> events, BinaryStringTable table)
        {
            writer.Write(events?.Count ?? 0);
            if (events == null)
                return;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                writer.Write(evt.ut);
                writer.Write(evt.partPersistentId);
                writer.Write((int)evt.eventType);
                writer.Write(table.GetIndex(evt.partName));
                writer.Write(evt.value);
                writer.Write(evt.moduleIndex);
            }
        }

        private static void ReadPartEventList(BinaryReader reader, List<PartEvent> events, List<string> stringTable)
        {
            int count = ReadBoundedCount(reader, 1, "part-event-list");
            for (int i = 0; i < count; i++)
            {
                events.Add(new PartEvent
                {
                    ut = reader.ReadDouble(),
                    partPersistentId = reader.ReadUInt32(),
                    eventType = (PartEventType)reader.ReadInt32(),
                    partName = ReadIndexedString(reader, stringTable) ?? string.Empty,
                    value = reader.ReadSingle(),
                    moduleIndex = reader.ReadInt32()
                });
            }
        }

        private static void WriteFlagEventList(BinaryWriter writer, List<FlagEvent> events, BinaryStringTable table)
        {
            writer.Write(events?.Count ?? 0);
            if (events == null)
                return;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                writer.Write(evt.ut);
                writer.Write(table.GetIndex(evt.flagSiteName));
                writer.Write(table.GetIndex(evt.placedBy));
                writer.Write(table.GetIndex(evt.plaqueText));
                writer.Write(table.GetIndex(evt.flagURL));
                writer.Write(evt.latitude);
                writer.Write(evt.longitude);
                writer.Write(evt.altitude);
                writer.Write(evt.rotX);
                writer.Write(evt.rotY);
                writer.Write(evt.rotZ);
                writer.Write(evt.rotW);
                writer.Write(table.GetIndex(evt.bodyName));
            }
        }

        private static void ReadFlagEventList(BinaryReader reader, List<FlagEvent> events, List<string> stringTable)
        {
            int count = ReadBoundedCount(reader, 1, "flag-event-list");
            for (int i = 0; i < count; i++)
            {
                events.Add(new FlagEvent
                {
                    ut = reader.ReadDouble(),
                    flagSiteName = ReadIndexedString(reader, stringTable) ?? string.Empty,
                    placedBy = ReadIndexedString(reader, stringTable) ?? string.Empty,
                    plaqueText = ReadIndexedString(reader, stringTable) ?? string.Empty,
                    flagURL = ReadIndexedString(reader, stringTable) ?? string.Empty,
                    latitude = reader.ReadDouble(),
                    longitude = reader.ReadDouble(),
                    altitude = reader.ReadDouble(),
                    rotX = reader.ReadSingle(),
                    rotY = reader.ReadSingle(),
                    rotZ = reader.ReadSingle(),
                    rotW = reader.ReadSingle(),
                    bodyName = ReadIndexedString(reader, stringTable) ?? "Kerbin"
                });
            }
        }

        private static void WriteSegmentEventList(BinaryWriter writer, List<SegmentEvent> events, BinaryStringTable table)
        {
            writer.Write(events?.Count ?? 0);
            if (events == null)
                return;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                writer.Write(evt.ut);
                writer.Write((int)evt.type);
                writer.Write(table.GetNullableIndex(evt.details));
            }
        }

        private static void ReadSegmentEventList(BinaryReader reader, List<SegmentEvent> events, List<string> stringTable)
        {
            int count = ReadBoundedCount(reader, 1, "segment-event-list");
            for (int i = 0; i < count; i++)
            {
                events.Add(new SegmentEvent
                {
                    ut = reader.ReadDouble(),
                    type = (SegmentEventType)reader.ReadInt32(),
                    details = ReadNullableIndexedString(reader, stringTable)
                });
            }
        }

        private static void WriteTrackSections(BinaryWriter writer, List<TrackSection> tracks, BinaryStringTable table, int binaryVersion, ref SparsePointWriteStats stats)
        {
            writer.Write(tracks?.Count ?? 0);
            if (tracks == null)
                return;

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                writer.Write((int)track.environment);
                writer.Write((int)track.referenceFrame);
                writer.Write(track.startUT);
                writer.Write(track.endUT);
                writer.Write(track.anchorVesselId);
                string anchorRecordingId = string.IsNullOrEmpty(track.anchorRecordingId)
                    ? null
                    : track.anchorRecordingId;
                writer.Write(table.GetNullableIndex(anchorRecordingId));
                writer.Write(track.sampleRateHz);
                writer.Write((int)track.source);
                writer.Write(track.boundaryDiscontinuityMeters);
                writer.Write(track.minAltitude);
                writer.Write(track.maxAltitude);
                writer.Write(track.isBoundarySeam);
                WritePointList(writer, track.frames, table, binaryVersion, ref stats);
                WritePointList(writer, track.bodyFixedFrames, table, binaryVersion, ref stats);
                WriteOrbitSegmentList(writer, track.checkpoints, table, binaryVersion);
            }
        }

        private static void ReadTrackSections(BinaryReader reader, List<TrackSection> tracks, List<string> stringTable, int binaryVersion, ref SparsePointReadStats stats)
        {
            int count = ReadBoundedCount(reader, 1, "track-sections");
            for (int i = 0; i < count; i++)
            {
                var track = new TrackSection
                {
                    environment = (SegmentEnvironment)reader.ReadInt32(),
                    referenceFrame = (ReferenceFrame)reader.ReadInt32(),
                    startUT = reader.ReadDouble(),
                    endUT = reader.ReadDouble(),
                    anchorVesselId = reader.ReadUInt32(),
                    anchorRecordingId = ReadNullableIndexedString(reader, stringTable),
                    sampleRateHz = reader.ReadSingle(),
                    source = (TrackSectionSource)reader.ReadInt32(),
                    boundaryDiscontinuityMeters = reader.ReadSingle(),
                    minAltitude = reader.ReadSingle(),
                    maxAltitude = reader.ReadSingle(),
                    isBoundarySeam = reader.ReadBoolean(),
                    frames = new List<TrajectoryPoint>(),
                    bodyFixedFrames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>()
                };

                ReadPointList(reader, track.frames, stringTable, binaryVersion, ref stats);
                ReadPointList(reader, track.bodyFixedFrames, stringTable, binaryVersion, ref stats);
                ReadOrbitSegmentList(reader, track.checkpoints, stringTable, binaryVersion);
                tracks.Add(track);
            }
        }

        private static void WriteSparsePointList(BinaryWriter writer, List<TrajectoryPoint> points, BinaryStringTable table, int binaryVersion, ref SparsePointWriteStats stats)
        {
            SparsePointListPlan plan = BuildSparsePointListPlan(points);
            writer.Write(plan.ListFlags);

            if (!plan.Enabled)
            {
                for (int i = 0; i < points.Count; i++)
                    WritePoint(writer, points[i], table, binaryVersion);
                return;
            }

            if (plan.HasBodyDefault)
                writer.Write(table.GetIndex(plan.DefaultBodyName));
            if (plan.HasFundsDefault)
                writer.Write(plan.DefaultFunds);
            if (plan.HasScienceDefault)
                writer.Write(plan.DefaultScience);
            if (plan.HasReputationDefault)
                writer.Write(plan.DefaultReputation);

            stats.SparsePointLists++;
            stats.SparsePoints += points.Count;

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                writer.Write(pt.ut);
                writer.Write(pt.latitude);
                writer.Write(pt.longitude);
                writer.Write(pt.altitude);
                writer.Write(pt.rotation.x);
                writer.Write(pt.rotation.y);
                writer.Write(pt.rotation.z);
                writer.Write(pt.rotation.w);
                writer.Write(pt.velocity.x);
                writer.Write(pt.velocity.y);
                writer.Write(pt.velocity.z);

                byte pointFlags = 0;
                if (plan.HasBodyDefault && !string.Equals(pt.bodyName, plan.DefaultBodyName, StringComparison.Ordinal))
                    pointFlags |= SparsePointOverrideBody;
                if (plan.HasFundsDefault && pt.funds != plan.DefaultFunds)
                    pointFlags |= SparsePointOverrideFunds;
                if (plan.HasScienceDefault && pt.science != plan.DefaultScience)
                    pointFlags |= SparsePointOverrideScience;
                if (plan.HasReputationDefault && pt.reputation != plan.DefaultReputation)
                    pointFlags |= SparsePointOverrideReputation;

                writer.Write(pointFlags);

                if (plan.HasBodyDefault)
                {
                    if ((pointFlags & SparsePointOverrideBody) != 0)
                        writer.Write(table.GetIndex(pt.bodyName));
                    else
                        stats.OmittedBody++;
                }
                else
                {
                    writer.Write(table.GetIndex(pt.bodyName));
                }

                if (plan.HasFundsDefault)
                {
                    if ((pointFlags & SparsePointOverrideFunds) != 0)
                        writer.Write(pt.funds);
                    else
                        stats.OmittedFunds++;
                }
                else
                {
                    writer.Write(pt.funds);
                }

                if (plan.HasScienceDefault)
                {
                    if ((pointFlags & SparsePointOverrideScience) != 0)
                        writer.Write(pt.science);
                    else
                        stats.OmittedScience++;
                }
                else
                {
                    writer.Write(pt.science);
                }

                if (plan.HasReputationDefault)
                {
                    if ((pointFlags & SparsePointOverrideReputation) != 0)
                        writer.Write(pt.reputation);
                    else
                        stats.OmittedReputation++;
                }
                else
                {
                    writer.Write(pt.reputation);
                }

                writer.Write(pt.recordedGroundClearance);
                writer.Write(pt.flags);
            }
        }

        private static void ReadSparsePointList(BinaryReader reader, List<TrajectoryPoint> points, List<string> stringTable, int count, int binaryVersion, ref SparsePointReadStats stats)
        {
            byte listFlags = reader.ReadByte();
            if ((listFlags & SparsePointListFlagEnabled) == 0)
            {
                for (int i = 0; i < count; i++)
                    points.Add(ReadPoint(reader, stringTable, binaryVersion));
                return;
            }

            string defaultBodyName = null;
            double defaultFunds = 0;
            float defaultScience = 0;
            float defaultReputation = 0;

            bool hasBodyDefault = (listFlags & SparsePointListFlagBodyDefault) != 0;
            bool hasFundsDefault = (listFlags & SparsePointListFlagFundsDefault) != 0;
            bool hasScienceDefault = (listFlags & SparsePointListFlagScienceDefault) != 0;
            bool hasReputationDefault = (listFlags & SparsePointListFlagReputationDefault) != 0;

            if (hasBodyDefault)
                defaultBodyName = ReadIndexedString(reader, stringTable);
            if (hasFundsDefault)
                defaultFunds = reader.ReadDouble();
            if (hasScienceDefault)
                defaultScience = reader.ReadSingle();
            if (hasReputationDefault)
                defaultReputation = reader.ReadSingle();

            stats.SparsePointLists++;
            stats.SparsePoints += count;

            for (int i = 0; i < count; i++)
            {
                var pt = new TrajectoryPoint
                {
                    ut = reader.ReadDouble(),
                    latitude = reader.ReadDouble(),
                    longitude = reader.ReadDouble(),
                    altitude = reader.ReadDouble(),
                    rotation = new Quaternion(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()),
                    velocity = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle())
                };

                byte pointFlags = reader.ReadByte();

                if (hasBodyDefault)
                {
                    if ((pointFlags & SparsePointOverrideBody) != 0)
                        pt.bodyName = ReadIndexedString(reader, stringTable);
                    else
                    {
                        pt.bodyName = defaultBodyName;
                        stats.DefaultedBody++;
                    }
                }
                else
                {
                    pt.bodyName = ReadIndexedString(reader, stringTable);
                }

                if (hasFundsDefault)
                {
                    if ((pointFlags & SparsePointOverrideFunds) != 0)
                        pt.funds = reader.ReadDouble();
                    else
                    {
                        pt.funds = defaultFunds;
                        stats.DefaultedFunds++;
                    }
                }
                else
                {
                    pt.funds = reader.ReadDouble();
                }

                if (hasScienceDefault)
                {
                    if ((pointFlags & SparsePointOverrideScience) != 0)
                        pt.science = reader.ReadSingle();
                    else
                    {
                        pt.science = defaultScience;
                        stats.DefaultedScience++;
                    }
                }
                else
                {
                    pt.science = reader.ReadSingle();
                }

                if (hasReputationDefault)
                {
                    if ((pointFlags & SparsePointOverrideReputation) != 0)
                        pt.reputation = reader.ReadSingle();
                    else
                    {
                        pt.reputation = defaultReputation;
                        stats.DefaultedReputation++;
                    }
                }
                else
                {
                    pt.reputation = reader.ReadSingle();
                }

                pt.recordedGroundClearance = reader.ReadDouble();
                pt.flags = reader.ReadByte();

                points.Add(pt);
            }
        }

        private static SparsePointListPlan BuildSparsePointListPlan(List<TrajectoryPoint> points)
        {
            var plan = new SparsePointListPlan();
            if (points == null || points.Count == 0)
                return plan;

            string bestBody = FindMostCommonString(points, pt => pt.bodyName, out int bodyMatches);
            double bestFunds = FindMostCommonDouble(points, pt => pt.funds, out int fundsMatches);
            float bestScience = FindMostCommonFloat(points, pt => pt.science, out int scienceMatches);
            float bestRep = FindMostCommonFloat(points, pt => pt.reputation, out int repMatches);

            int localNetSavings = 0;

            if (!string.IsNullOrEmpty(bestBody) && ((4 * bodyMatches) - 4) > 0)
            {
                plan.HasBodyDefault = true;
                plan.DefaultBodyName = bestBody;
                localNetSavings += (4 * bodyMatches) - 4;
            }

            if (((8 * fundsMatches) - 8) > 0)
            {
                plan.HasFundsDefault = true;
                plan.DefaultFunds = bestFunds;
                localNetSavings += (8 * fundsMatches) - 8;
            }

            if (((4 * scienceMatches) - 4) > 0)
            {
                plan.HasScienceDefault = true;
                plan.DefaultScience = bestScience;
                localNetSavings += (4 * scienceMatches) - 4;
            }

            if (((4 * repMatches) - 4) > 0)
            {
                plan.HasReputationDefault = true;
                plan.DefaultReputation = bestRep;
                localNetSavings += (4 * repMatches) - 4;
            }

            if (localNetSavings <= (points.Count + 1))
                return plan;

            plan.Enabled = true;
            plan.ListFlags = SparsePointListFlagEnabled;
            if (plan.HasBodyDefault)
                plan.ListFlags |= SparsePointListFlagBodyDefault;
            if (plan.HasFundsDefault)
                plan.ListFlags |= SparsePointListFlagFundsDefault;
            if (plan.HasScienceDefault)
                plan.ListFlags |= SparsePointListFlagScienceDefault;
            if (plan.HasReputationDefault)
                plan.ListFlags |= SparsePointListFlagReputationDefault;
            return plan;
        }

        private static List<string> ReadStringTable(BinaryReader reader)
        {
            // Bound count first (each string entry consumes at least one
            // byte -- the BinaryWriter-encoded 7-bit length prefix is one
            // byte for a zero-length string). Then build the list without
            // a capacity hint so a corrupt-but-bounded count cannot
            // pre-allocate a giant string[] before the per-entry reads
            // fail.
            int count = ReadBoundedCount(reader, 1, "string-table");
            var strings = new List<string>();
            for (int i = 0; i < count; i++)
                strings.Add(reader.ReadString());
            return strings;
        }

        private static string ReadIndexedString(BinaryReader reader, List<string> stringTable)
        {
            int index = reader.ReadInt32();
            if (index < 0 || index >= stringTable.Count)
                throw new InvalidDataException($"String table index out of range: {index}");
            return stringTable[index];
        }

        private static string ReadNullableIndexedString(BinaryReader reader, List<string> stringTable)
        {
            int index = reader.ReadInt32();
            if (index < 0)
                return null;
            if (index >= stringTable.Count)
                throw new InvalidDataException($"String table index out of range: {index}");
            return stringTable[index];
        }

        private static int CountNonDefaultSectionSources(List<TrackSection> tracks)
        {
            if (tracks == null || tracks.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].source != TrackSectionSource.Active)
                    count++;
            }

            return count;
        }

        private static string FindMostCommonString(List<TrajectoryPoint> points, Func<TrajectoryPoint, string> selector, out int matches)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string best = null;
            matches = 0;

            for (int i = 0; i < points.Count; i++)
            {
                string value = selector(points[i]) ?? string.Empty;
                int count;
                counts.TryGetValue(value, out count);
                count++;
                counts[value] = count;
                if (count > matches)
                {
                    matches = count;
                    best = value;
                }
            }

            return best;
        }

        private static double FindMostCommonDouble(List<TrajectoryPoint> points, Func<TrajectoryPoint, double> selector, out int matches)
        {
            var counts = new Dictionary<double, int>();
            double best = 0;
            matches = 0;

            for (int i = 0; i < points.Count; i++)
            {
                double value = selector(points[i]);
                int count;
                counts.TryGetValue(value, out count);
                count++;
                counts[value] = count;
                if (count > matches)
                {
                    matches = count;
                    best = value;
                }
            }

            return best;
        }

        private static float FindMostCommonFloat(List<TrajectoryPoint> points, Func<TrajectoryPoint, float> selector, out int matches)
        {
            var counts = new Dictionary<float, int>();
            float best = 0;
            matches = 0;

            for (int i = 0; i < points.Count; i++)
            {
                float value = selector(points[i]);
                int count;
                counts.TryGetValue(value, out count);
                count++;
                counts[value] = count;
                if (count > matches)
                {
                    matches = count;
                    best = value;
                }
            }

            return best;
        }

        private struct SparsePointListPlan
        {
            public bool Enabled;
            public byte ListFlags;
            public bool HasBodyDefault;
            public string DefaultBodyName;
            public bool HasFundsDefault;
            public double DefaultFunds;
            public bool HasScienceDefault;
            public float DefaultScience;
            public bool HasReputationDefault;
            public float DefaultReputation;
        }

        private struct SparsePointWriteStats
        {
            public int SparsePointLists;
            public int SparsePoints;
            public int OmittedBody;
            public int OmittedFunds;
            public int OmittedScience;
            public int OmittedReputation;
        }

        private struct SparsePointReadStats
        {
            public int SparsePointLists;
            public int SparsePoints;
            public int DefaultedBody;
            public int DefaultedFunds;
            public int DefaultedScience;
            public int DefaultedReputation;
        }

        private sealed class BinaryStringTable
        {
            private readonly Dictionary<string, int> indexes = new Dictionary<string, int>();

            internal List<string> Strings { get; } = new List<string>();

            internal void Register(string value)
            {
                if (value == null || indexes.ContainsKey(value))
                    return;

                indexes[value] = Strings.Count;
                Strings.Add(value);
            }

            internal int GetIndex(string value)
            {
                if (value == null)
                    throw new InvalidOperationException("Non-null string expected in binary string table.");

                int index;
                if (!indexes.TryGetValue(value, out index))
                    throw new InvalidOperationException($"String '{value}' missing from binary string table.");
                return index;
            }

            internal int GetNullableIndex(string value)
            {
                if (value == null)
                    return -1;

                return GetIndex(value);
            }
        }
    }
}
