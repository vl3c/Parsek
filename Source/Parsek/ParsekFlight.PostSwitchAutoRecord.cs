using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The post-switch auto-record watch's types: its decisions, triggers, suppression reasons, the
    // watch state and the orbit snapshot. The behaviour is ParsekFlightPostSwitchAutoRecord.
    public partial class ParsekFlight
    {
        internal enum PostSwitchAutoRecordStartDecision
        {
            None,
            PromoteTrackedRecording,
            RestoreAndPromoteTrackedRecording,
            StartFreshRecording
        }

        internal enum PostSwitchAutoRecordArmDecision
        {
            None,
            ArmTrackedBackgroundMember,
            ArmOutsider
        }

        internal enum PostSwitchAutoRecordTrigger
        {
            None,
            EngineActivity,
            SustainedRcsActivity,
            AttitudeChange,
            CrewChange,
            ResourceChange,
            PartStateChange,
            LandedMotion,
            OrbitChange
        }

        internal enum PostSwitchAutoRecordSuppressionReason
        {
            None,
            Disabled,
            AlreadyRecording,
            NoActiveVessel,
            ActiveVesselMismatch,
            GhostMapVessel,
            RestoreInProgress,
            PendingTransition,
            WarpActive,
            PackedOrOnRails
        }

        internal struct PostSwitchOrbitSnapshot
        {
            public bool IsValid;
            public string BodyName;
            public double SemiMajorAxis;
            public double Eccentricity;
            public double Inclination;
            public double LongitudeOfAscendingNode;
            public double ArgumentOfPeriapsis;
        }

        internal sealed class PostSwitchAutoRecordState
        {
            public uint VesselPid;
            public string VesselName;
            public double ArmedAtUt;
            public string ArmedReason;
            public bool TrackedInActiveTree;
            public string FreshStartParentRecordingId;
            public bool BaselineCaptured;
            public double BaselineCapturedUt;
            public double ComparisonsReadyUt;
            public double NextManifestEvaluationUt;
            public Vector3d BaselineWorldPosition;
            public Quaternion BaselineWorldRotation;
            public TrajectoryPoint? BaselineSeedPoint;
            public PostSwitchOrbitSnapshot BaselineOrbit;
            public double AttitudeThresholdExceededAtUt = double.NaN;
            public Dictionary<string, ResourceAmount> BaselineResources;
            public Dictionary<string, InventoryItem> BaselineInventory;
            public Dictionary<string, int> BaselineCrew;
            public HashSet<string> BaselinePartStateTokens = new HashSet<string>();
            public bool ModuleCachesDirty;
            public int CachedPartCount;
            public List<(Part part, ModuleEngines engine, int moduleIndex)> CachedEngines
                = new List<(Part, ModuleEngines, int)>();
            public HashSet<ulong> ActiveEngineKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> EngineThrottleMap = new Dictionary<ulong, float>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> CachedRcsModules
                = new List<(Part, ModuleRCS, int)>();
            public Dictionary<ulong, int> RcsFrameCountMap = new Dictionary<ulong, int>();
            public HashSet<ulong> ActiveRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> RcsThrottleMap = new Dictionary<ulong, float>();
        }
    }
}
