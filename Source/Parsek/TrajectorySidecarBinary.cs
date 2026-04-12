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
        BinaryV2 = 1
    }

    internal struct TrajectorySidecarProbe
    {
        public bool Success;
        public bool Supported;
        public TrajectorySidecarEncoding Encoding;
        public int FormatVersion;
        public int SidecarEpoch;
        public string RecordingId;
        public ConfigNode LegacyNode;
        public string FailureReason;
    }

    internal static class TrajectorySidecarBinary
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PRKB");
        private const int SupportedBinaryVersion = 2;
        private const byte FlagSectionAuthoritative = 1 << 0;

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
                    int b = stream.ReadByte();
                    if (b != Magic[i])
                        return false;
                }
            }

            return true;
        }

        internal static bool TryProbe(string path, out TrajectorySidecarProbe probe)
        {
            probe = default(TrajectorySidecarProbe);

            if (!File.Exists(path))
            {
                probe.FailureReason = "file missing";
                return false;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < Magic.Length + sizeof(int) + sizeof(int))
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
                int sidecarEpoch = reader.ReadInt32();
                string recordingId = reader.ReadString();

                probe.Success = true;
                probe.Encoding = TrajectorySidecarEncoding.BinaryV2;
                probe.FormatVersion = formatVersion;
                probe.SidecarEpoch = sidecarEpoch;
                probe.RecordingId = recordingId;
                probe.Supported = formatVersion == SupportedBinaryVersion;
                probe.FailureReason = probe.Supported
                    ? null
                    : $"unsupported binary trajectory version {formatVersion}";
                return true;
            }
        }

        internal static void Write(string path, Recording rec, int sidecarEpoch)
        {
            if (rec == null)
                throw new ArgumentNullException(nameof(rec));

            bool sectionAuthoritative = rec.TrackSections != null && rec.TrackSections.Count > 0;
            var table = BuildStringTable(rec);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(SupportedBinaryVersion);
                writer.Write(sidecarEpoch);
                writer.Write(rec.RecordingId ?? string.Empty);
                writer.Write(sectionAuthoritative ? FlagSectionAuthoritative : (byte)0);

                writer.Write(table.Strings.Count);
                for (int i = 0; i < table.Strings.Count; i++)
                    writer.Write(table.Strings[i] ?? string.Empty);

                WritePointList(writer, sectionAuthoritative ? null : rec.Points, table);
                WriteOrbitSegmentList(writer, sectionAuthoritative ? null : rec.OrbitSegments, table);
                WritePartEventList(writer, rec.PartEvents, table);
                WriteFlagEventList(writer, rec.FlagEvents, table);
                WriteSegmentEventList(writer, rec.SegmentEvents, table);
                WriteTrackSections(writer, rec.TrackSections, table);
                writer.Flush();

                FileIOUtils.SafeWriteBytes(stream.ToArray(), path, "RecordingStore");
            }

            if (!RecordingStore.SuppressLogging)
            {
                int nonDefaultSectionSources = CountNonDefaultSectionSources(rec.TrackSections);
                ParsekLog.Verbose("RecordingStore",
                    $"WriteBinaryTrajectoryFile: recording={rec.RecordingId} version={SupportedBinaryVersion} " +
                    $"sectionAuthoritative={sectionAuthoritative} strings={table.Strings.Count} " +
                    $"points={(sectionAuthoritative ? 0 : rec.Points.Count)} orbitSegments={(sectionAuthoritative ? 0 : rec.OrbitSegments.Count)} " +
                    $"trackSections={rec.TrackSections?.Count ?? 0} nonDefaultSectionSources={nonDefaultSectionSources}");
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

                ReadPointList(reader, rec.Points, stringTable);
                ReadOrbitSegmentList(reader, rec.OrbitSegments, stringTable);
                ReadPartEventList(reader, rec.PartEvents, stringTable);
                ReadFlagEventList(reader, rec.FlagEvents, stringTable);
                ReadSegmentEventList(reader, rec.SegmentEvents, stringTable);
                ReadTrackSections(reader, rec.TrackSections, stringTable);

                rec.RecordingFormatVersion = probe.FormatVersion;
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
                            $"dedupedOrbitCopies={dedupedOrbitCopies}");
                    }
                }
                else if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"ReadBinaryTrajectoryFile: recording={rec.RecordingId} version={probe.FormatVersion} " +
                        $"used flat fallback path points={rec.Points.Count} orbitSegments={rec.OrbitSegments.Count} " +
                        $"trackSections={rec.TrackSections.Count}");
                }
            }
        }

        private static void SkipHeader(BinaryReader reader)
        {
            for (int i = 0; i < Magic.Length; i++)
                reader.ReadByte();

            reader.ReadInt32(); // formatVersion
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

                    if (section.checkpoints != null)
                    {
                        for (int i = 0; i < section.checkpoints.Count; i++)
                            table.Register(section.checkpoints[i].bodyName);
                    }
                }
            }

            return table;
        }

        private static void WritePointList(BinaryWriter writer, List<TrajectoryPoint> points, BinaryStringTable table)
        {
            writer.Write(points?.Count ?? 0);
            if (points == null)
                return;

            for (int i = 0; i < points.Count; i++)
                WritePoint(writer, points[i], table);
        }

        private static void ReadPointList(BinaryReader reader, List<TrajectoryPoint> points, List<string> stringTable)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                points.Add(ReadPoint(reader, stringTable));
        }

        private static void WritePoint(BinaryWriter writer, TrajectoryPoint pt, BinaryStringTable table)
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
        }

        private static TrajectoryPoint ReadPoint(BinaryReader reader, List<string> stringTable)
        {
            return new TrajectoryPoint
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
        }

        private static void WriteOrbitSegmentList(BinaryWriter writer, List<OrbitSegment> segments, BinaryStringTable table)
        {
            writer.Write(segments?.Count ?? 0);
            if (segments == null)
                return;

            for (int i = 0; i < segments.Count; i++)
                WriteOrbitSegment(writer, segments[i], table);
        }

        private static void ReadOrbitSegmentList(BinaryReader reader, List<OrbitSegment> segments, List<string> stringTable)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                segments.Add(ReadOrbitSegment(reader, stringTable));
        }

        private static void WriteOrbitSegment(BinaryWriter writer, OrbitSegment seg, BinaryStringTable table)
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
            writer.Write(seg.orbitalFrameRotation.x);
            writer.Write(seg.orbitalFrameRotation.y);
            writer.Write(seg.orbitalFrameRotation.z);
            writer.Write(seg.orbitalFrameRotation.w);
            writer.Write(seg.angularVelocity.x);
            writer.Write(seg.angularVelocity.y);
            writer.Write(seg.angularVelocity.z);
        }

        private static OrbitSegment ReadOrbitSegment(BinaryReader reader, List<string> stringTable)
        {
            return new OrbitSegment
            {
                startUT = reader.ReadDouble(),
                endUT = reader.ReadDouble(),
                inclination = reader.ReadDouble(),
                eccentricity = reader.ReadDouble(),
                semiMajorAxis = reader.ReadDouble(),
                longitudeOfAscendingNode = reader.ReadDouble(),
                argumentOfPeriapsis = reader.ReadDouble(),
                meanAnomalyAtEpoch = reader.ReadDouble(),
                epoch = reader.ReadDouble(),
                bodyName = ReadIndexedString(reader, stringTable) ?? "Kerbin",
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
            int count = reader.ReadInt32();
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
            int count = reader.ReadInt32();
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
            int count = reader.ReadInt32();
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

        private static void WriteTrackSections(BinaryWriter writer, List<TrackSection> tracks, BinaryStringTable table)
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
                writer.Write(track.sampleRateHz);
                writer.Write((int)track.source);
                writer.Write(track.boundaryDiscontinuityMeters);
                writer.Write(track.minAltitude);
                writer.Write(track.maxAltitude);
                WritePointList(writer, track.frames, table);
                WriteOrbitSegmentList(writer, track.checkpoints, table);
            }
        }

        private static void ReadTrackSections(BinaryReader reader, List<TrackSection> tracks, List<string> stringTable)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var track = new TrackSection
                {
                    environment = (SegmentEnvironment)reader.ReadInt32(),
                    referenceFrame = (ReferenceFrame)reader.ReadInt32(),
                    startUT = reader.ReadDouble(),
                    endUT = reader.ReadDouble(),
                    anchorVesselId = reader.ReadUInt32(),
                    sampleRateHz = reader.ReadSingle(),
                    source = (TrackSectionSource)reader.ReadInt32(),
                    boundaryDiscontinuityMeters = reader.ReadSingle(),
                    minAltitude = reader.ReadSingle(),
                    maxAltitude = reader.ReadSingle(),
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>()
                };

                ReadPointList(reader, track.frames, stringTable);
                ReadOrbitSegmentList(reader, track.checkpoints, stringTable);
                tracks.Add(track);
            }
        }

        private static List<string> ReadStringTable(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var strings = new List<string>(count);
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
