using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Tests.Generators
{
    public class RecordingBuilder
    {
        private readonly string vesselName;
        private readonly List<ConfigNode> points = new List<ConfigNode>();
        private readonly List<ConfigNode> orbitSegments = new List<ConfigNode>();
        private readonly List<ConfigNode> partEvents = new List<ConfigNode>();
        private readonly List<ConfigNode> flagEvents = new List<ConfigNode>();
        private readonly List<TrackSection> trackSections = new List<TrackSection>();
        private readonly List<SegmentEvent> segmentEvents = new List<SegmentEvent>();
        private List<ControllerInfo> controllers;
        private bool isDebris;
        private ConfigNode vesselSnapshot;
        private ConfigNode ghostVisualSnapshot;
        private uint spawnedPid;
        private int lastResIdx = -1;
        private string parentRecordingId;
        private string evaCrewName;
        private string recordingId;
        private string chainId;
        private int chainIndex = -1;
        private int chainBranch = -1;
        private bool loopPlayback;
        private double loopIntervalSeconds = 0.0;
        private string segmentPhase;
        private string segmentBodyName;
        private bool playbackEnabled = true;
        private List<string> recordingGroups = new List<string>();
        private string rewindSaveFileName;
        private double rewindReservedFunds;
        private double rewindReservedScience;
        private float rewindReservedRep;
        private int? terminalState;
        private double terrainHeightAtEnd = double.NaN;
        private Dictionary<string, ResourceAmount> startResources;
        private Dictionary<string, ResourceAmount> endResources;

        // Default rotation for points that don't specify one explicitly
        private float defaultRotX, defaultRotY, defaultRotZ;
        private float defaultRotW = 1;
        private bool hasDefaultRotation;
        private int formatVersion = RecordingStore.CurrentRecordingFormatVersion;

        public RecordingBuilder(string vesselName)
        {
            this.vesselName = vesselName;
        }

        /// <summary>
        /// Set default rotation for all subsequent AddPoint calls that use
        /// identity rotation (0,0,0,1). Points with explicit non-identity
        /// rotation values are unaffected.
        /// </summary>
        public RecordingBuilder WithDefaultRotation(float x, float y, float z, float w)
        {
            defaultRotX = x;
            defaultRotY = y;
            defaultRotZ = z;
            defaultRotW = w;
            hasDefaultRotation = true;
            return this;
        }

        public RecordingBuilder WithFormatVersion(int version)
        {
            formatVersion = version;
            return this;
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
            // Apply default rotation if caller left rotation at identity (0,0,0,1)
            if (hasDefaultRotation && rotX == 0 && rotY == 0 && rotZ == 0 && rotW == 1)
            {
                rotX = defaultRotX;
                rotY = defaultRotY;
                rotZ = defaultRotZ;
                rotW = defaultRotW;
            }

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

        /// <summary>
        /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9): adds a synthetic
        /// structural-event snapshot point with the
        /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/> bit set on
        /// <see cref="TrajectoryPoint.flags"/>. Mirrors the ConfigNode shape of
        /// <see cref="AddPoint"/> plus a <c>flags</c> value the binary codec
        /// round-trips at v10. Test fixtures using this method must also pin
        /// <see cref="WithFormatVersion"/> to <see cref="RecordingStore.StructuralEventFlagFormatVersion"/>
        /// (or rely on the default of <see cref="RecordingStore.CurrentRecordingFormatVersion"/>),
        /// otherwise the v9-or-older binary writer drops the byte.
        /// </summary>
        public RecordingBuilder WithStructuralEventSnapshot(
            double ut, double lat, double lon, double alt,
            string body = "Kerbin",
            float rotX = 0, float rotY = 0, float rotZ = 0, float rotW = 1,
            double funds = 0, float science = 0, float rep = 0)
        {
            // Apply default rotation if caller left rotation at identity (0,0,0,1)
            if (hasDefaultRotation && rotX == 0 && rotY == 0 && rotZ == 0 && rotW == 1)
            {
                rotX = defaultRotX;
                rotY = defaultRotY;
                rotZ = defaultRotZ;
                rotW = defaultRotW;
            }

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
            // Phase 9: flags byte. ConfigNode-form fixtures don't round-trip
            // through the binary codec — tests asserting on the binary path
            // build TrajectoryPoint directly. The "flags" value is recorded
            // here so any future text-codec extension can pick it up.
            pt.AddValue("flags",
                ((byte)TrajectoryPointFlags.StructuralEventSnapshot).ToString(ic));
            points.Add(pt);
            return this;
        }

        public RecordingBuilder AddOrbitSegment(double startUT, double endUT,
            double inc = 0, double ecc = 0, double sma = 700000,
            double lan = 0, double argPe = 0, double mna = 0, double epoch = 0,
            string body = "Kerbin",
            float ofrX = 0, float ofrY = 0, float ofrZ = 0, float ofrW = 0,
            float avX = 0, float avY = 0, float avZ = 0,
            bool isPredicted = false)
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
            seg.AddValue("isPredicted", isPredicted ? "True" : "False");
            if (ofrX != 0 || ofrY != 0 || ofrZ != 0 || ofrW != 0)
            {
                seg.AddValue("ofrX", ofrX.ToString("R", ic));
                seg.AddValue("ofrY", ofrY.ToString("R", ic));
                seg.AddValue("ofrZ", ofrZ.ToString("R", ic));
                seg.AddValue("ofrW", ofrW.ToString("R", ic));
            }
            if (avX != 0 || avY != 0 || avZ != 0)
            {
                seg.AddValue("avX", avX.ToString("R", ic));
                seg.AddValue("avY", avY.ToString("R", ic));
                seg.AddValue("avZ", avZ.ToString("R", ic));
            }
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

        public RecordingBuilder AddPartEvent(double ut, uint pid, int type, string partName = "",
            float value = 0f, int moduleIndex = 0)
        {
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode("PART_EVENT");
            node.AddValue("ut", ut.ToString("R", ic));
            node.AddValue("pid", pid.ToString(ic));
            node.AddValue("type", type.ToString(ic));
            node.AddValue("part", partName);
            node.AddValue("value", value.ToString("R", ic));
            node.AddValue("midx", moduleIndex.ToString(ic));
            partEvents.Add(node);
            return this;
        }

        public RecordingBuilder AddFlagEvent(double ut, string name, string placedBy,
            string plaqueText, string flagURL, double lat, double lon, double alt,
            float rotX = 0f, float rotY = 0f, float rotZ = 0f, float rotW = 1f,
            string body = "Kerbin")
        {
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode("FLAG_EVENT");
            node.AddValue("ut", ut.ToString("R", ic));
            node.AddValue("name", name ?? "");
            node.AddValue("placedBy", placedBy ?? "");
            node.AddValue("plaqueText", plaqueText ?? "");
            node.AddValue("flagURL", flagURL ?? "");
            node.AddValue("lat", lat.ToString("R", ic));
            node.AddValue("lon", lon.ToString("R", ic));
            node.AddValue("alt", alt.ToString("R", ic));
            node.AddValue("rotX", rotX.ToString("R", ic));
            node.AddValue("rotY", rotY.ToString("R", ic));
            node.AddValue("rotZ", rotZ.ToString("R", ic));
            node.AddValue("rotW", rotW.ToString("R", ic));
            node.AddValue("body", body);
            flagEvents.Add(node);
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

        public RecordingBuilder WithChainId(string id)
        {
            chainId = id;
            return this;
        }

        public RecordingBuilder WithChainIndex(int index)
        {
            chainIndex = index;
            return this;
        }

        public RecordingBuilder WithChainBranch(int branch)
        {
            chainBranch = branch;
            return this;
        }

        public RecordingBuilder WithLoopPlayback(bool loop = true, double intervalSeconds = 0.0)
        {
            loopPlayback = loop;
            loopIntervalSeconds = intervalSeconds;
            return this;
        }

        public RecordingBuilder WithSegmentPhase(string phase)
        {
            segmentPhase = phase;
            return this;
        }

        public RecordingBuilder WithSegmentBodyName(string body)
        {
            segmentBodyName = body;
            return this;
        }

        public RecordingBuilder WithPlaybackEnabled(bool enabled)
        {
            playbackEnabled = enabled;
            return this;
        }

        public RecordingBuilder WithRecordingGroup(string group)
        {
            if (!recordingGroups.Contains(group))
                recordingGroups.Add(group);
            return this;
        }

        public RecordingBuilder WithRewindSave(string fileName,
            double funds = 0, double science = 0, float rep = 0)
        {
            rewindSaveFileName = fileName;
            rewindReservedFunds = funds;
            rewindReservedScience = science;
            rewindReservedRep = rep;
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

        public RecordingBuilder WithTerminalState(int terminalState)
        {
            this.terminalState = terminalState;
            return this;
        }

        public RecordingBuilder WithTerrainHeightAtEnd(double height)
        {
            terrainHeightAtEnd = height;
            return this;
        }

        internal RecordingBuilder WithStartResources(Dictionary<string, ResourceAmount> resources)
        {
            startResources = resources;
            return this;
        }

        internal RecordingBuilder WithEndResources(Dictionary<string, ResourceAmount> resources)
        {
            endResources = resources;
            return this;
        }

        // --- v6 TrackSection builder methods ---

        /// <summary>
        /// Adds a TrackSection with full control over all parameters.
        /// </summary>
        public RecordingBuilder AddTrackSection(
            SegmentEnvironment env, ReferenceFrame refFrame, TrackSectionSource source,
            double startUT, double endUT,
            List<TrajectoryPoint> frames = null, List<OrbitSegment> checkpoints = null,
            uint anchorVesselId = 0, float sampleRateHz = 0f,
            bool isBoundarySeam = false)
        {
            var section = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                source = source,
                startUT = startUT,
                endUT = endUT,
                frames = frames ?? new List<TrajectoryPoint>(),
                checkpoints = checkpoints ?? new List<OrbitSegment>(),
                anchorVesselId = anchorVesselId,
                sampleRateHz = sampleRateHz,
                isBoundarySeam = isBoundarySeam
            };
            trackSections.Add(section);
            return this;
        }

        /// <summary>
        /// Adds a fully-specified TrackSection, copying nested lists so fixtures can safely
        /// reuse shared boundary points and metadata without leaking mutable references.
        /// </summary>
        public RecordingBuilder AddTrackSection(TrackSection section)
        {
            var copy = section;
            copy.frames = section.frames != null
                ? new List<TrajectoryPoint>(section.frames)
                : new List<TrajectoryPoint>();
            copy.checkpoints = section.checkpoints != null
                ? new List<OrbitSegment>(section.checkpoints)
                : new List<OrbitSegment>();
            trackSections.Add(copy);
            return this;
        }

        /// <summary>
        /// Convenience: creates an ATMOSPHERIC + ABSOLUTE + Active section with no frames.
        /// Frames can be added separately or the section can serve as a time-range marker.
        /// </summary>
        public RecordingBuilder AddAtmosphericSection(double startUT, double endUT)
        {
            return AddTrackSection(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                startUT, endUT, sampleRateHz: 10.0f);
        }

        /// <summary>
        /// Convenience: creates an EXO_BALLISTIC + ORBITAL_CHECKPOINT + Checkpoint section
        /// with a single orbital checkpoint.
        /// </summary>
        public RecordingBuilder AddOrbitalCheckpointSection(double startUT, double endUT, OrbitSegment checkpoint)
        {
            return AddTrackSection(
                SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, TrackSectionSource.Checkpoint,
                startUT, endUT,
                checkpoints: new List<OrbitSegment> { checkpoint },
                sampleRateHz: 0.1f);
        }

        // --- v6 SegmentEvent builder methods ---

        /// <summary>
        /// Adds a SegmentEvent with explicit type, UT, and optional details.
        /// </summary>
        public RecordingBuilder AddSegmentEvent(SegmentEventType type, double ut, string details = null)
        {
            segmentEvents.Add(new SegmentEvent
            {
                type = type,
                ut = ut,
                details = details
            });
            return this;
        }

        /// <summary>
        /// Convenience: adds a ControllerChange segment event.
        /// </summary>
        public RecordingBuilder AddControllerChangeEvent(double ut, string details)
        {
            return AddSegmentEvent(SegmentEventType.ControllerChange, ut, details);
        }

        /// <summary>
        /// Convenience: adds a PartDestroyed segment event with part name and PID in details.
        /// </summary>
        public RecordingBuilder AddPartDestroyedEvent(double ut, string partName, uint partPid)
        {
            return AddSegmentEvent(SegmentEventType.PartDestroyed, ut,
                $"{partName}:{partPid}");
        }

        // --- v6 ControllerInfo builder methods ---

        /// <summary>
        /// Sets the full list of controllers from the given array.
        /// </summary>
        public RecordingBuilder WithControllers(params ControllerInfo[] ctrls)
        {
            controllers = new List<ControllerInfo>(ctrls);
            return this;
        }

        /// <summary>
        /// Adds a single controller to the controllers list.
        /// </summary>
        public RecordingBuilder AddController(string type, string partName, uint partPid)
        {
            if (controllers == null)
                controllers = new List<ControllerInfo>();
            controllers.Add(new ControllerInfo
            {
                type = type,
                partName = partName,
                partPersistentId = partPid
            });
            return this;
        }

        /// <summary>
        /// Marks this recording as debris (vessel has no controller parts).
        /// </summary>
        public RecordingBuilder AsDebris()
        {
            isDebris = true;
            return this;
        }

        /// <summary>
        /// Returns the track sections list (for test assertions).
        /// </summary>
        public List<TrackSection> GetTrackSections() => trackSections;

        /// <summary>
        /// Returns the segment events list (for test assertions).
        /// </summary>
        public List<SegmentEvent> GetSegmentEvents() => segmentEvents;

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
            node.AddValue("version", formatVersion.ToString());
            node.AddValue("recordingId", GetRecordingId());
            if (formatVersion >= 1 && trackSections.Count > 0)
            {
                bool sectionAuthoritative = RecordingStore.HasCompleteTrackSectionPayloadForFlatSync(
                    trackSections, allowRelativeSections: true);
                node.AddValue("sectionAuthoritative", sectionAuthoritative ? "True" : "False");
            }

            foreach (var pt in points)
                node.AddNode(pt);
            foreach (var seg in orbitSegments)
                node.AddNode(seg);
            foreach (var pe in partEvents)
                node.AddNode(pe);
            foreach (var fe in flagEvents)
                node.AddNode(fe);

            // v6: serialize segment events and track sections
            if (segmentEvents.Count > 0)
                RecordingStore.SerializeSegmentEvents(node, segmentEvents);
            if (trackSections.Count > 0)
                RecordingStore.SerializeTrackSections(node, trackSections);

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
            node.AddValue("recordingFormatVersion", formatVersion.ToString());
            var snapshotModeRecording = new Recording
            {
                VesselSnapshot = vesselSnapshot,
                GhostVisualSnapshot = ghostVisualSnapshot ?? vesselSnapshot
            };
            GhostSnapshotMode ghostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(snapshotModeRecording);
            if (ghostSnapshotMode != GhostSnapshotMode.Unspecified)
                node.AddValue("ghostSnapshotMode", ghostSnapshotMode.ToString());
            node.AddValue("loopPlayback", loopPlayback.ToString());
            node.AddValue("loopIntervalSeconds", GetLoopIntervalSeconds().ToString("R", CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(parentRecordingId))
                node.AddValue("parentRecordingId", parentRecordingId);
            if (!string.IsNullOrEmpty(evaCrewName))
                node.AddValue("evaCrewName", evaCrewName);

            if (!string.IsNullOrEmpty(chainId))
                node.AddValue("chainId", chainId);
            if (chainIndex >= 0)
                node.AddValue("chainIndex", chainIndex);
            if (chainBranch > 0)
                node.AddValue("chainBranch", chainBranch);

            if (spawnedPid != 0)
                node.AddValue("spawnedPid", spawnedPid);

            node.AddValue("lastResIdx", lastResIdx);

            // UI grouping tags (multi-group membership)
            for (int g = 0; g < recordingGroups.Count; g++)
                node.AddValue("recordingGroup", recordingGroups[g]);
            if (!string.IsNullOrEmpty(segmentPhase))
                node.AddValue("segmentPhase", segmentPhase);
            if (!string.IsNullOrEmpty(segmentBodyName))
                node.AddValue("segmentBodyName", segmentBodyName);
            if (!playbackEnabled)
                node.AddValue("playbackEnabled", playbackEnabled.ToString());

            if (!string.IsNullOrEmpty(rewindSaveFileName))
            {
                var ic2 = CultureInfo.InvariantCulture;
                node.AddValue("rewindSave", rewindSaveFileName);
                node.AddValue("rewindResFunds", rewindReservedFunds.ToString("R", ic2));
                node.AddValue("rewindResSci", rewindReservedScience.ToString("R", ic2));
                node.AddValue("rewindResRep", rewindReservedRep.ToString("R", ic2));
            }

            if (terminalState.HasValue)
                node.AddValue("terminalState", terminalState.Value.ToString(CultureInfo.InvariantCulture));

            if (!double.IsNaN(terrainHeightAtEnd))
                node.AddValue("terrainHeightAtEnd", terrainHeightAtEnd.ToString("R", CultureInfo.InvariantCulture));

            // v6: Controller info
            if (controllers != null)
            {
                var ic2 = CultureInfo.InvariantCulture;
                for (int i = 0; i < controllers.Count; i++)
                {
                    ConfigNode ctrlNode = node.AddNode("CONTROLLER");
                    ctrlNode.AddValue("type", controllers[i].type ?? "");
                    ctrlNode.AddValue("part", controllers[i].partName ?? "");
                    ctrlNode.AddValue("pid", controllers[i].partPersistentId.ToString(ic2));
                }
            }

            // v6: IsDebris
            if (isDebris)
                node.AddValue("isDebris", isDebris.ToString());

            // Resource manifests (Phase 11)
            SerializeResourceManifestInto(node);

            return node;
        }

        /// <summary>Returns the start resources dictionary (may be null).</summary>
        internal Dictionary<string, ResourceAmount> GetStartResources() => startResources;

        /// <summary>Returns the end resources dictionary (may be null).</summary>
        internal Dictionary<string, ResourceAmount> GetEndResources() => endResources;

        /// <summary>
        /// Serializes StartResources/EndResources into a RESOURCE_MANIFEST node on parent,
        /// matching the format used by RecordingStore.SerializeResourceManifest.
        /// </summary>
        private void SerializeResourceManifestInto(ConfigNode parent)
        {
            bool hasStart = startResources != null && startResources.Count > 0;
            bool hasEnd = endResources != null && endResources.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            var ic = CultureInfo.InvariantCulture;
            ConfigNode manifestNode = parent.AddNode("RESOURCE_MANIFEST");

            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in startResources.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in endResources.Keys) keys.Add(k);

            foreach (var name in keys)
            {
                ConfigNode resNode = manifestNode.AddNode("RESOURCE");
                resNode.AddValue("name", name);

                if (hasStart && startResources.TryGetValue(name, out var startRa))
                {
                    resNode.AddValue("startAmount", startRa.amount.ToString("R", ic));
                    resNode.AddValue("startMax", startRa.maxAmount.ToString("R", ic));
                }

                if (hasEnd && endResources.TryGetValue(name, out var endRa))
                {
                    resNode.AddValue("endAmount", endRa.amount.ToString("R", ic));
                    resNode.AddValue("endMax", endRa.maxAmount.ToString("R", ic));
                }
            }
        }

        /// <summary>Returns the vessel name.</summary>
        public string GetVesselName() => vesselName;

        /// <summary>Returns the UT of the first point, or 0 if no points.</summary>
        public double GetStartUT()
        {
            if (points.Count == 0) return 0;
            var ic = CultureInfo.InvariantCulture;
            double ut;
            double.TryParse(points[0].GetValue("ut"), System.Globalization.NumberStyles.Float, ic, out ut);
            return ut;
        }

        /// <summary>Returns the vessel snapshot (may be null).</summary>
        public ConfigNode GetVesselSnapshot() => vesselSnapshot;

        /// <summary>Returns the ghost visual snapshot (may be null).</summary>
        public ConfigNode GetGhostVisualSnapshot() => ghostVisualSnapshot ?? vesselSnapshot;

        /// <summary>
        /// Builds a RECORDING ConfigNode with inline POINT/snapshot data.
        /// Used by unit tests for ConfigNode-level assertions, not for save file injection.
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

            foreach (var fe in flagEvents)
                node.AddNode(fe);

            if (!string.IsNullOrEmpty(parentRecordingId))
                node.AddValue("parentRecordingId", parentRecordingId);
            if (!string.IsNullOrEmpty(evaCrewName))
                node.AddValue("evaCrewName", evaCrewName);

            if (!string.IsNullOrEmpty(chainId))
                node.AddValue("chainId", chainId);
            if (chainIndex >= 0)
                node.AddValue("chainIndex", chainIndex);
            if (chainBranch > 0)
                node.AddValue("chainBranch", chainBranch);

            node.AddValue("loopPlayback", loopPlayback.ToString());
            node.AddValue("loopIntervalSeconds", GetLoopIntervalSeconds().ToString("R", CultureInfo.InvariantCulture));

            if (vesselSnapshot != null)
                node.AddNode("VESSEL_SNAPSHOT", vesselSnapshot);

            if (ghostVisualSnapshot != null)
                node.AddNode("GHOST_VISUAL_SNAPSHOT", ghostVisualSnapshot);

            if (spawnedPid != 0)
                node.AddValue("spawnedPid", spawnedPid);

            node.AddValue("lastResIdx", lastResIdx);

            if (!string.IsNullOrEmpty(rewindSaveFileName))
            {
                var ic2 = CultureInfo.InvariantCulture;
                node.AddValue("rewindSave", rewindSaveFileName);
                node.AddValue("rewindResFunds", rewindReservedFunds.ToString("R", ic2));
                node.AddValue("rewindResSci", rewindReservedScience.ToString("R", ic2));
                node.AddValue("rewindResRep", rewindReservedRep.ToString("R", ic2));
            }

            if (terminalState.HasValue)
                node.AddValue("terminalState", terminalState.Value.ToString(CultureInfo.InvariantCulture));

            if (!double.IsNaN(terrainHeightAtEnd))
                node.AddValue("terrainHeightAtEnd", terrainHeightAtEnd.ToString("R", CultureInfo.InvariantCulture));

            // v6: Controller info
            if (controllers != null)
            {
                var icCtrl = CultureInfo.InvariantCulture;
                for (int i = 0; i < controllers.Count; i++)
                {
                    ConfigNode ctrlNode = node.AddNode("CONTROLLER");
                    ctrlNode.AddValue("type", controllers[i].type ?? "");
                    ctrlNode.AddValue("part", controllers[i].partName ?? "");
                    ctrlNode.AddValue("pid", controllers[i].partPersistentId.ToString(icCtrl));
                }
            }

            // v6: IsDebris
            if (isDebris)
                node.AddValue("isDebris", isDebris.ToString());

            // v6: Segment events and track sections (inline for v2)
            if (segmentEvents.Count > 0)
                RecordingStore.SerializeSegmentEvents(node, segmentEvents);
            if (trackSections.Count > 0)
                RecordingStore.SerializeTrackSections(node, trackSections);

            // Resource manifests (Phase 11)
            SerializeResourceManifestInto(node);

            return node;
        }

        /// <summary>Returns the rewind save file name (may be null).</summary>
        public string GetRewindSaveFileName() => rewindSaveFileName;

        /// <summary>Returns the UT of the last point, or 0 if no points.</summary>
        public double GetEndUT()
        {
            if (points.Count == 0) return 0;
            var ic = CultureInfo.InvariantCulture;
            double ut;
            double.TryParse(points[points.Count - 1].GetValue("ut"), NumberStyles.Float, ic, out ut);
            return ut;
        }

        /// <summary>Returns the format version.</summary>
        public int GetFormatVersion() => formatVersion;

        /// <summary>Returns whether loop playback is enabled.</summary>
        public bool GetLoopPlayback() => loopPlayback;

        /// <summary>
        /// Returns the loop interval in seconds for serialization. When loop playback is
        /// enabled but the caller left the interval below <see cref="LoopTiming.MinCycleDuration"/>
        /// (typically the builder's default of 0.0 — pre-#381 "relaunch with no gap"), auto-derive
        /// the period from trajectory duration so written fixtures never carry a degenerate value
        /// that would spam <c>ResolveLoopInterval</c>'s clamp warning at playback (#412).
        /// Mirrors the UI default where an unset period loops seamlessly at the recording's own
        /// duration. Falls back to <see cref="LoopTiming.DefaultLoopIntervalSeconds"/> if
        /// the trajectory is empty or still below the floor.
        /// </summary>
        public double GetLoopIntervalSeconds()
        {
            if (!loopPlayback)
                return loopIntervalSeconds;
            if (loopIntervalSeconds >= LoopTiming.MinCycleDuration)
                return loopIntervalSeconds;

            if (points.Count >= 2)
            {
                double duration = GetEndUT() - GetStartUT();
                if (duration >= LoopTiming.MinCycleDuration)
                    return duration;
            }
            return LoopTiming.DefaultLoopIntervalSeconds;
        }

        /// <summary>Returns the raw loop interval field as set by the caller (no auto-derivation).</summary>
        public double GetRawLoopIntervalSeconds() => loopIntervalSeconds;

        /// <summary>Returns the terminal state (null if not set).</summary>
        public int? GetTerminalState() => terminalState;

        /// <summary>Returns the terrain height at end.</summary>
        public double GetTerrainHeightAtEnd() => terrainHeightAtEnd;

        /// <summary>Returns the recording groups list.</summary>
        public List<string> GetRecordingGroups() => recordingGroups;

        /// <summary>Returns the segment phase (may be null).</summary>
        public string GetSegmentPhase() => segmentPhase;

        /// <summary>Returns the segment body name (may be null).</summary>
        public string GetSegmentBodyName() => segmentBodyName;

        /// <summary>Returns whether playback is enabled.</summary>
        public bool GetPlaybackEnabled() => playbackEnabled;

        /// <summary>Returns the chain ID (may be null).</summary>
        public string GetChainId() => chainId;

        /// <summary>Returns the chain index (-1 if not set).</summary>
        public int GetChainIndex() => chainIndex;

        /// <summary>Returns the chain branch (-1 if not set).</summary>
        public int GetChainBranch() => chainBranch;

        /// <summary>Returns the parent recording ID (may be null).</summary>
        public string GetParentRecordingId() => parentRecordingId;

        /// <summary>Returns the EVA crew name (may be null).</summary>
        public string GetEvaCrewName() => evaCrewName;

        /// <summary>Returns the rewind reserved funds.</summary>
        public double GetRewindReservedFunds() => rewindReservedFunds;

        /// <summary>Returns the rewind reserved science.</summary>
        public double GetRewindReservedScience() => rewindReservedScience;

        /// <summary>Returns the rewind reserved reputation.</summary>
        public float GetRewindReservedRep() => rewindReservedRep;

        /// <summary>Returns the controllers list (may be null).</summary>
        public List<ControllerInfo> GetControllers() => controllers;

        /// <summary>Returns whether this recording is debris.</summary>
        public bool GetIsDebris() => isDebris;
    }
}
