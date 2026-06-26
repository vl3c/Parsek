using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    internal partial class BackgroundRecorder
    {
        #region Inner Classes

        // INVARIANT: on-rails BG vessels never produce env-classified per-frame TrackSections.
        // This class deliberately omits the `currentTrackSection` / `trackSections` /
        // `environmentHysteresis` fields that BackgroundVesselState (loaded mode) carries.
        // An on-rails BG vessel grazing atmosphere across N orbits therefore cannot generate
        // optimizer-splittable Atmospheric<->ExoBallistic toggles. The packed path may emit
        // OrbitalCheckpoint sections when closed orbit segments are committed, but those
        // sections are orbit-only bridge payloads, not per-frame environment classifications.
        // See `OnBackgroundPhysicsFrame`'s early-return on `bgVessel.packed`. Adding a
        // TrackSection field or EnvironmentHysteresis here would resurrect the eccentric-orbit
        // chain-explosion failure mode flagged in
        // `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16; do not.
        private class BackgroundOnRailsState
        {
            public uint vesselPid;
            public string recordingId;
            public bool hasOpenOrbitSegment;
            public OrbitSegment currentOrbitSegment;
            public bool isLanded;
            public double lastExplicitEndUpdate;
        }

        private class BackgroundVesselState
        {
            public uint vesselPid;
            public string recordingId;

            // Adaptive sampling state
            public double lastRecordedUT = -1;
            public Vector3 lastRecordedVelocity;
            public Quaternion lastWorldRotation;
            public bool hasLastWorldRotation;

            // Proximity-based sample interval tracking
            public double currentSampleInterval = ProximityRateSelector.OutOfRangeInterval;
            public double highFidelitySamplingUntilUT = double.NaN;
            public string highFidelitySamplingReason;
            public bool loggedFirstDebrisOrdinarySample;

            // Part event tracking (mirrors FlightRecorder's instance fields)
            public Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>();
            public HashSet<uint> jettisonedShrouds = new HashSet<uint>();
            public Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
            public Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
            public HashSet<uint> extendedDeployables = new HashSet<uint>();
            public HashSet<uint> lightsOn = new HashSet<uint>();
            public HashSet<uint> blinkingLights = new HashSet<uint>();
            public Dictionary<uint, float> lightBlinkRates = new Dictionary<uint, float>();
            public HashSet<uint> deployedGear = new HashSet<uint>();
            public HashSet<uint> openCargoBays = new HashSet<uint>();
            public HashSet<uint> deployedFairings = new HashSet<uint>();
            public HashSet<ulong> deployedLadders = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimationGroups = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimateGenericModules = new HashSet<ulong>();
            public HashSet<ulong> deployedAeroSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedControlSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedRobotArmScannerModules = new HashSet<ulong>();
            public Dictionary<ulong, HeatLevel> animateHeatLevels = new Dictionary<ulong, HeatLevel>();

            // Engine/RCS/robotic caches
            public List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
            public HashSet<ulong> activeEngineKeys = new HashSet<ulong>();
            public HashSet<ulong> allEngineKeys = new HashSet<ulong>(); // all engine modules, active + inactive (#298)
            public Dictionary<ulong, float> lastThrottle = new Dictionary<ulong, float>();
            public HashSet<ulong> loggedEngineModuleKeys = new HashSet<ulong>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
            public HashSet<ulong> activeRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRcsThrottle = new Dictionary<ulong, float>();
            public Dictionary<ulong, int> rcsActiveFrameCount = new Dictionary<ulong, int>();
            public HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
            public List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
            public HashSet<ulong> activeRoboticKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRoboticPosition = new Dictionary<ulong, float>();
            public Dictionary<ulong, double> lastRoboticSampleUT = new Dictionary<ulong, double>();
            public HashSet<ulong> loggedRoboticModuleKeys = new HashSet<ulong>();

            // Environment tracking (TrackSection management)
            public EnvironmentHysteresis environmentHysteresis;
            public TrackSection currentTrackSection;
            public bool trackSectionActive;
            public List<TrackSection> trackSections = new List<TrackSection>();
            public bool isRelativeMode;
            public string currentAnchorRecordingId;
            public RecordingAnchorCandidate currentAnchorCandidate;
            public bool hasCurrentAnchorCandidate;
            public ProximitySamplingTier debrisProximityTier = ProximitySamplingTier.None;
            public double debrisProximityDistanceMeters = double.NaN;
            public string debrisProximityReason;

            // Phase 7: per-SurfaceMobile-section ground-clearance accumulators
            // (background side mirror of FlightRecorder's foreground state).
            // Reset on `StartBackgroundTrackSection`, populated by the
            // background sampler for Absolute SurfaceMobile/SurfaceStationary
            // sections, and summarised at section close so a `.prec` written
            // from background recording surfaces the same clearance
            // distribution diagnostic as foreground (HR-9 visibility parity).
            // Field names retain the original SurfaceMobile wording.
            public int surfaceMobileSamplesThisSection;
            public double surfaceMobileMinClearanceThisSection = double.NaN;
            public double surfaceMobileMaxClearanceThisSection = double.NaN;
            public double surfaceMobileClearanceSumThisSection;

            // Per-frame warp flags for the current section, index-aligned with
            // currentTrackSection.frames (flag[i] == true means frame i was
            // sampled under physics time-warp). Used at section close to
            // classify each large gap: a gap touching a warp sample is a
            // structurally-expected jump (Verbose), a large gap whose both ends
            // were at 1x is a genuine dropped-sample signal (WARN). On-rails BG
            // samples never reach the per-frame tick (OnBackgroundPhysicsFrame
            // early-returns on bgVessel.packed), so physics warp is the only
            // signal here. Reset on StartBackgroundTrackSection, trimmed in
            // lockstep with frames in TrimParentAtBranchBoundary.
            public readonly List<bool> sectionFrameWarpFlags = new List<bool>();

            // Part destruction/decoupling tracking
            public HashSet<uint> decoupledPartIds = new HashSet<uint>();

            // Diagnostic guards (prevent log spam, one per module type)
            public HashSet<ulong> loggedLadderClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimationGroupClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateGenericClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAeroSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedControlSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedRobotArmScannerClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateHeatClassificationMisses = new HashSet<ulong>();
            public HashSet<uint> loggedCargoBayDeployIndexIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayAnimationIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayClosedPositionIssues = new HashSet<uint>();
            public HashSet<uint> loggedFairingReadFailures = new HashSet<uint>();
        }

        #endregion
    }
}
