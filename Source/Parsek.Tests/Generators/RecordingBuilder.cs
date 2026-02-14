using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Generators
{
    public class RecordingBuilder
    {
        private readonly string vesselName;
        private readonly List<ConfigNode> points = new List<ConfigNode>();
        private readonly List<ConfigNode> orbitSegments = new List<ConfigNode>();
        private readonly List<ConfigNode> partEvents = new List<ConfigNode>();
        private ConfigNode vesselSnapshot;
        private ConfigNode ghostVisualSnapshot;
        private uint spawnedPid;
        private int lastResIdx = -1;
        private string parentRecordingId;
        private string evaCrewName;
        private string recordingId;

        public RecordingBuilder(string vesselName)
        {
            this.vesselName = vesselName;
        }

        public RecordingBuilder WithRecordingId(string id)
        {
            recordingId = id;
            return this;
        }

        public RecordingBuilder AddPoint(double ut, double lat, double lon, double alt,
            string body = "Kerbin",
            float rotX = 0, float rotY = 0, float rotZ = 0, float rotW = 1,
            double funds = 0, float science = 0, float rep = 0)
        {
            var ic = CultureInfo.InvariantCulture;
            var pt = new ConfigNode("POINT");
            pt.AddValue("ut", ut.ToString("R", ic));
            pt.AddValue("lat", lat.ToString("R", ic));
            pt.AddValue("lon", lon.ToString("R", ic));
            pt.AddValue("alt", alt.ToString("R", ic));
            pt.AddValue("rotX", rotX.ToString("R", ic));
            pt.AddValue("rotY", rotY.ToString("R", ic));
            pt.AddValue("rotZ", rotZ.ToString("R", ic));
            pt.AddValue("rotW", rotW.ToString("R", ic));
            pt.AddValue("body", body);
            pt.AddValue("funds", funds.ToString("R", ic));
            pt.AddValue("science", science.ToString("R", ic));
            pt.AddValue("rep", rep.ToString("R", ic));
            points.Add(pt);
            return this;
        }

        public RecordingBuilder AddOrbitSegment(double startUT, double endUT,
            double inc = 0, double ecc = 0, double sma = 700000,
            double lan = 0, double argPe = 0, double mna = 0, double epoch = 0,
            string body = "Kerbin")
        {
            var ic = CultureInfo.InvariantCulture;
            var seg = new ConfigNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", startUT.ToString("R", ic));
            seg.AddValue("endUT", endUT.ToString("R", ic));
            seg.AddValue("inc", inc.ToString("R", ic));
            seg.AddValue("ecc", ecc.ToString("R", ic));
            seg.AddValue("sma", sma.ToString("R", ic));
            seg.AddValue("lan", lan.ToString("R", ic));
            seg.AddValue("argPe", argPe.ToString("R", ic));
            seg.AddValue("mna", mna.ToString("R", ic));
            seg.AddValue("epoch", epoch.ToString("R", ic));
            seg.AddValue("body", body);
            orbitSegments.Add(seg);
            return this;
        }

        public RecordingBuilder WithVesselSnapshot(ConfigNode snapshot)
        {
            vesselSnapshot = snapshot;
            return this;
        }

        public RecordingBuilder WithVesselSnapshot(VesselSnapshotBuilder builder)
        {
            vesselSnapshot = builder.Build();
            return this;
        }

        public RecordingBuilder WithSpawnedPid(uint pid)
        {
            spawnedPid = pid;
            return this;
        }

        public RecordingBuilder WithLastResIdx(int idx)
        {
            lastResIdx = idx;
            return this;
        }

        public RecordingBuilder AddPartEvent(double ut, uint pid, int type, string partName = "")
        {
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode("PART_EVENT");
            node.AddValue("ut", ut.ToString("R", ic));
            node.AddValue("pid", pid.ToString(ic));
            node.AddValue("type", type.ToString(ic));
            node.AddValue("part", partName);
            partEvents.Add(node);
            return this;
        }

        public RecordingBuilder WithParentRecordingId(string id)
        {
            parentRecordingId = id;
            return this;
        }

        public RecordingBuilder WithEvaCrewName(string name)
        {
            evaCrewName = name;
            return this;
        }

        public RecordingBuilder WithGhostVisualSnapshot(ConfigNode snapshot)
        {
            ghostVisualSnapshot = snapshot;
            return this;
        }

        public RecordingBuilder WithGhostVisualSnapshot(VesselSnapshotBuilder builder)
        {
            ghostVisualSnapshot = builder.Build();
            return this;
        }

        /// <summary>
        /// Returns the recording ID (auto-generates one if not set).
        /// </summary>
        public string GetRecordingId()
        {
            if (string.IsNullOrEmpty(recordingId))
                recordingId = System.Guid.NewGuid().ToString("N");
            return recordingId;
        }

        /// <summary>
        /// Builds the trajectory data as a PARSEK_RECORDING ConfigNode for .prec files.
        /// </summary>
        public ConfigNode BuildTrajectoryNode()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "3");
            node.AddValue("recordingId", GetRecordingId());

            foreach (var pt in points)
                node.AddNode(pt);
            foreach (var seg in orbitSegments)
                node.AddNode(seg);
            foreach (var pe in partEvents)
                node.AddNode(pe);

            return node;
        }

        /// <summary>
        /// Builds a v3 metadata-only RECORDING node (no inline POINT/snapshot data).
        /// </summary>
        public ConfigNode BuildV3Metadata()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("vesselName", vesselName);
            node.AddValue("pointCount", points.Count);
            node.AddValue("recordingId", GetRecordingId());
            node.AddValue("recordingFormatVersion", "3");

            if (!string.IsNullOrEmpty(parentRecordingId))
                node.AddValue("parentRecordingId", parentRecordingId);
            if (!string.IsNullOrEmpty(evaCrewName))
                node.AddValue("evaCrewName", evaCrewName);

            if (spawnedPid != 0)
                node.AddValue("spawnedPid", spawnedPid);

            node.AddValue("lastResIdx", lastResIdx);

            return node;
        }

        /// <summary>Returns the vessel snapshot (may be null).</summary>
        public ConfigNode GetVesselSnapshot() => vesselSnapshot;

        /// <summary>Returns the ghost visual snapshot (may be null).</summary>
        public ConfigNode GetGhostVisualSnapshot() => ghostVisualSnapshot ?? vesselSnapshot;

        /// <summary>
        /// Builds a v2-format RECORDING node with inline POINT/snapshot data (existing behavior).
        /// </summary>
        public ConfigNode Build()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("vesselName", vesselName);
            node.AddValue("pointCount", points.Count);

            foreach (var pt in points)
                node.AddNode(pt);

            foreach (var seg in orbitSegments)
                node.AddNode(seg);

            foreach (var pe in partEvents)
                node.AddNode(pe);

            if (!string.IsNullOrEmpty(parentRecordingId))
                node.AddValue("parentRecordingId", parentRecordingId);
            if (!string.IsNullOrEmpty(evaCrewName))
                node.AddValue("evaCrewName", evaCrewName);

            if (vesselSnapshot != null)
                node.AddNode("VESSEL_SNAPSHOT", vesselSnapshot);

            if (spawnedPid != 0)
                node.AddValue("spawnedPid", spawnedPid);

            node.AddValue("lastResIdx", lastResIdx);

            return node;
        }
    }
}
