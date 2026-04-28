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
        BinaryV2 = 1,
        BinaryV3 = 2
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
        private const int LegacyBinaryVersion = 2;
        // #411 follow-up: the binary sidecar layout changed at v3 (sparse point lists) and
        // again at v5 (OrbitSegment.isPredicted). The v4 bump is metadata-only
        // (loopIntervalSeconds semantic change, see
        // RecordingStore.LaunchToLaunchLoopIntervalFormatVersion), so the v3 and v4 bytes are
        // identical. Keep each version anchor explicit so decode gates can distinguish the
        // v4 no-flag layout from the v5 predicted-flag layout.
        private const int SparsePointBinaryVersion = 3;
        private const int LoopIntervalBinaryVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion;
        private const int PredictedOrbitSegmentBinaryVersion = RecordingStore.PredictedOrbitSegmentFormatVersion;
        private const int RelativeLocalFrameBinaryVersion = RecordingStore.RelativeLocalFrameFormatVersion;
        private const int RelativeAbsoluteShadowBinaryVersion = RecordingStore.RelativeAbsoluteShadowFormatVersion;
        // Internal so the cross-codec sync test in TrajectorySidecarBinaryTests can pin
        // RecordingStore.BoundarySeamFlagFormatVersion == this constant. Drift between the
        // two would silently break v8 round-trip — the binary write/read paths gate on
        // this value, but the public RecordingStore.BoundarySeamFlagFormatVersion drives
        // the recording's RecordingFormatVersion stamp and the version-selection ladder.
        internal const int BoundarySeamFlagBinaryVersion = RecordingStore.BoundarySeamFlagFormatVersion;
        private const int CurrentBinaryVersion = BoundarySeamFlagBinaryVersion;
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
                probe.Encoding = GetBinaryEncoding(formatVersion);
                probe.FormatVersion = formatVersion;
                probe.SidecarEpoch = sidecarEpoch;
                probe.RecordingId = recordingId;
                probe.Supported = IsSupportedBinaryVersion(formatVersion);
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

            bool sectionAuthoritative = RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(rec);
            var table = BuildStringTable(rec);
            int binaryVersion = rec.RecordingFormatVersion >= CurrentBinaryVersion
                ? CurrentBinaryVersion
                : rec.RecordingFormatVersion >= RelativeAbsoluteShadowBinaryVersion
                    ? RelativeAbsoluteShadowBinaryVersion
                : rec.RecordingFormatVersion >= RelativeLocalFrameBinaryVersion
                    ? RelativeLocalFrameBinaryVersion
                : rec.RecordingFormatVersion >= PredictedOrbitSegmentBinaryVersion
                    ? PredictedOrbitSegmentBinaryVersion
                : rec.RecordingFormatVersion >= LoopIntervalBinaryVersion
                    ? LoopIntervalBinaryVersion
                : rec.RecordingFormatVersion >= SparsePointBinaryVersion
                    ? SparsePointBinaryVersion
                    : LegacyBinaryVersion;
            SparsePointWriteStats stats = default(SparsePointWriteStats);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(binaryVersion);
                writer.Write(sidecarEpoch);
                writer.Write(rec.RecordingId ?? string.Empty);
                writer.Write(sectionAuthoritative ? FlagSectionAuthoritative : (byte)0);

                writer.Write(table.Strings.Count);
                for (int i = 0; i < table.Strings.Count; i++)
                    writer.Write(table.Strings[i] ?? string.Empty);

                WritePointList(writer, sectionAuthoritative ? null : rec.Points, table, binaryVersion, ref stats);
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
                    $"points={(sectionAuthoritative ? 0 : rec.Points.Count)} orbitSegments={(sectionAuthoritative ? 0 : rec.OrbitSegments.Count)} " +
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
                    probe.FormatVersion >= 1 &&
                    rec.TrackSections.Count > 0)
                {
                    healedMalformedFlatFallback = RecordingStore.TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                        rec, allowRelativeSections: true);
                }

                // #411 follow-up: promote-only. Never demote rec.RecordingFormatVersion from a
                // higher in-memory value (e.g. a v4 stamp set by the tree/scenario legacy loop
                // migration before sidecars were hydrated) back down to the on-disk binary
                // version. Demotion would cause MigrateLegacyLoopIntervalAfterHydration to fire
                // a second time against the already-migrated value and double the period.
                if (rec.RecordingFormatVersion < probe.FormatVersion)
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
                    if (section.absoluteFrames != null)
                    {
                        for (int i = 0; i < section.absoluteFrames.Count; i++)
                            table.Register(section.absoluteFrames[i].bodyName);
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
            return version == LegacyBinaryVersion
                || version == SparsePointBinaryVersion
                || version == LoopIntervalBinaryVersion
                || version == PredictedOrbitSegmentBinaryVersion
                || version == RelativeLocalFrameBinaryVersion
                || version == RelativeAbsoluteShadowBinaryVersion
                || version == CurrentBinaryVersion;
        }

        private static TrajectorySidecarEncoding GetBinaryEncoding(int version)
        {
            return version >= SparsePointBinaryVersion
                ? TrajectorySidecarEncoding.BinaryV3
                : TrajectorySidecarEncoding.BinaryV2;
        }

        private static void WritePointList(BinaryWriter writer, List<TrajectoryPoint> points, BinaryStringTable table, int binaryVersion, ref SparsePointWriteStats stats)
        {
            writer.Write(points?.Count ?? 0);
            if (points == null || points.Count == 0)
                return;

            if (binaryVersion >= SparsePointBinaryVersion)
            {
                WriteSparsePointList(writer, points, table, ref stats);
                return;
            }

            for (int i = 0; i < points.Count; i++)
                WritePoint(writer, points[i], table);
        }

        private static void ReadPointList(BinaryReader reader, List<TrajectoryPoint> points, List<string> stringTable, int binaryVersion, ref SparsePointReadStats stats)
        {
            int count = reader.ReadInt32();
            if (count == 0)
                return;

            if (binaryVersion >= SparsePointBinaryVersion)
            {
                ReadSparsePointList(reader, points, stringTable, count, ref stats);
                return;
            }

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
            int count = reader.ReadInt32();
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
            if (binaryVersion >= PredictedOrbitSegmentBinaryVersion)
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
            if (binaryVersion >= PredictedOrbitSegmentBinaryVersion)
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
                writer.Write(track.sampleRateHz);
                writer.Write((int)track.source);
                writer.Write(track.boundaryDiscontinuityMeters);
                writer.Write(track.minAltitude);
                writer.Write(track.maxAltitude);
                // v8: isBoundarySeam flag for Producer-C no-payload boundary seam. The byte
                // appears AFTER maxAltitude and BEFORE the frames list so v7 readers (which
                // expect frames immediately after maxAltitude) keep reading frames at the
                // correct offset. New readers gate on binaryVersion >= 8 and default-false on <8.
                if (binaryVersion >= BoundarySeamFlagBinaryVersion)
                    writer.Write(track.isBoundarySeam);
                WritePointList(writer, track.frames, table, binaryVersion, ref stats);
                if (binaryVersion >= RelativeAbsoluteShadowBinaryVersion)
                    WritePointList(writer, track.absoluteFrames, table, binaryVersion, ref stats);
                WriteOrbitSegmentList(writer, track.checkpoints, table, binaryVersion);
            }
        }

        private static void ReadTrackSections(BinaryReader reader, List<TrackSection> tracks, List<string> stringTable, int binaryVersion, ref SparsePointReadStats stats)
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
                    // v8: isBoundarySeam flag — gated on binaryVersion >= 8, default-false on <8.
                    // Reading happens BEFORE the frames list to preserve positional layout.
                    isBoundarySeam = (binaryVersion >= BoundarySeamFlagBinaryVersion) && reader.ReadBoolean(),
                    frames = new List<TrajectoryPoint>(),
                    absoluteFrames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>()
                };

                ReadPointList(reader, track.frames, stringTable, binaryVersion, ref stats);
                if (binaryVersion >= RelativeAbsoluteShadowBinaryVersion)
                    ReadPointList(reader, track.absoluteFrames, stringTable, binaryVersion, ref stats);
                ReadOrbitSegmentList(reader, track.checkpoints, stringTable, binaryVersion);
                tracks.Add(track);
            }
        }

        private static void WriteSparsePointList(BinaryWriter writer, List<TrajectoryPoint> points, BinaryStringTable table, ref SparsePointWriteStats stats)
        {
            SparsePointListPlan plan = BuildSparsePointListPlan(points);
            writer.Write(plan.ListFlags);

            if (!plan.Enabled)
            {
                for (int i = 0; i < points.Count; i++)
                    WritePoint(writer, points[i], table);
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
            }
        }

        private static void ReadSparsePointList(BinaryReader reader, List<TrajectoryPoint> points, List<string> stringTable, int count, ref SparsePointReadStats stats)
        {
            byte listFlags = reader.ReadByte();
            if ((listFlags & SparsePointListFlagEnabled) == 0)
            {
                for (int i = 0; i < count; i++)
                    points.Add(ReadPoint(reader, stringTable));
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
