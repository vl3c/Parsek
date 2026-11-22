using System.Collections.Generic;

namespace Parsek
{
    internal enum FinalizationCacheOwner
    {
        Unknown = 0,
        ActiveRecorder = 1,
        BackgroundLoaded = 2,
        BackgroundOnRails = 3
    }

    internal enum FinalizationCacheStatus
    {
        Empty = 0,
        Fresh = 1,
        Stale = 2,
        Failed = 3
    }

    internal struct RecordingFinalizationTerminalOrbit
    {
        public double inclination;
        public double eccentricity;
        public double semiMajorAxis;
        public double longitudeOfAscendingNode;
        public double argumentOfPeriapsis;
        public double meanAnomalyAtEpoch;
        public double epoch;
        public string bodyName;

        internal static RecordingFinalizationTerminalOrbit FromSegment(OrbitSegment segment)
        {
            return new RecordingFinalizationTerminalOrbit
            {
                inclination = segment.inclination,
                eccentricity = segment.eccentricity,
                semiMajorAxis = segment.semiMajorAxis,
                longitudeOfAscendingNode = segment.longitudeOfAscendingNode,
                argumentOfPeriapsis = segment.argumentOfPeriapsis,
                meanAnomalyAtEpoch = segment.meanAnomalyAtEpoch,
                epoch = segment.epoch,
                bodyName = segment.bodyName
            };
        }
    }

    internal sealed class RecordingFinalizationCache
    {
        // Identity fields are enforced by the applier before any recording is mutated.
        public string RecordingId;
        public uint VesselPersistentId;
        public FinalizationCacheOwner Owner;
        public FinalizationCacheStatus Status;

        // Refresh producers and diagnostics own these fields; the pure applier only
        // reports status/owner/reasons in logs.
        public double CachedAtUT = double.NaN;
        public float CachedAtRealtime;
        public string RefreshReason;
        public string DeclineReason;

        // Last-observed state is the producer-side digest used to skip redundant
        // refreshes and explain stale cache decisions.
        public double LastObservedUT = double.NaN;
        public string LastObservedBodyName;
        public Vessel.Situations LastSituation;
        public bool LastWasInAtmosphere;
        public bool LastHadMeaningfulThrust;
        public string LastObservedOrbitDigest;

        // Predicted terminal payload copied into a Recording only when a consumer
        // accepts the cache.
        public double TailStartsAtUT = double.NaN;
        public double TerminalUT = double.NaN;
        public TerminalState? TerminalState;
        public string TerminalBodyName;
        public RecordingFinalizationTerminalOrbit? TerminalOrbit;
        public SurfacePosition? TerminalPosition;
        public double? TerrainHeightAtEnd;

        public List<OrbitSegment> PredictedSegments = new List<OrbitSegment>();

        // A predicted chute descent (owner ruling Q4): body-fixed points from the refresh to
        // touchdown, appended by the applier as a TrackSectionSource.Predicted section. Empty
        // for every other ending. In memory only, like the rest of the cache.
        public List<TrajectoryPoint> PredictedDescentPoints = new List<TrajectoryPoint>();

        // A chute touchdown stock deleted the vessel ahead of: its Destroyed ending replays the
        // descent and ends without the explosion (Recording.QuietDestroyedEnd).
        public bool QuietEnd;

        // Owner ruling 2026-11-22: an Orbiting ending where the ground scan stopped checking
        // (ExtrapolationResult.endedAtGroundScanEnd); the applier stamps it as
        // Recording.GroundScanEndOrbit. In memory only, like the rest of the cache.
        public bool GroundScanEnd;
    }
}
