using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pre-computed policy decisions passed alongside each trajectory.
    /// The engine reads these flags instead of accessing Recording policy fields.
    /// Built by the host (ParsekFlight) before calling engine.UpdatePlayback().
    /// </summary>
    internal struct TrajectoryPlaybackFlags
    {
        /// <summary>Don't render this ghost (chain-suppressed, disabled, external vessel).</summary>
        public bool skipGhost;

        /// <summary>Not part of a recording tree (gates resource event firing).</summary>
        public bool isStandalone;

        /// <summary>Mid-chain segment — hold ghost at end position instead of destroying.</summary>
        public bool isMidChain;

        /// <summary>UT when the full chain sequence ends (for pastChainEnd check).</summary>
        public double chainEndUT;

        /// <summary>Pre-computed ShouldSpawnAtRecordingEnd result.</summary>
        public bool needsSpawn;

        /// <summary>This recording belongs to the currently active recording chain.</summary>
        public bool isActiveChainMember;

        /// <summary>The chain this belongs to is fully looping or fully disabled.</summary>
        public bool isChainLoopingOrDisabled;

        /// <summary>Segment phase label for logging (e.g., "Ascent [Kerbin]").</summary>
        public string segmentLabel;

        /// <summary>Recording identity key (for event payloads and logging).</summary>
        public string recordingId;

        /// <summary>Vessel persistent ID (for event payloads and logging).</summary>
        public uint vesselPersistentId;
    }

    /// <summary>
    /// Minimal per-frame data the engine needs from the host.
    /// Does not include any policy state — only physical context.
    /// </summary>
    internal struct FrameContext
    {
        /// <summary>Current universal time.</summary>
        public double currentUT;

        /// <summary>Current time warp rate (1.0 = normal).</summary>
        public float warpRate;

        /// <summary>Current time warp rate index (0 = no warp).</summary>
        public int warpRateIndex;

        /// <summary>Active vessel world position (for bubble-distance checks).</summary>
        public Vector3d activeVesselPos;

        /// <summary>Index of the watched ghost — exempt from soft cap and zone hiding. -1 if none.</summary>
        public int protectedIndex;

        /// <summary>Number of ghosts managed outside the engine (chain ghosts etc.) for soft cap accounting.</summary>
        public int externalGhostCount;

        /// <summary>Auto loop interval from settings (engine doesn't read ParsekSettings).</summary>
        public double autoLoopIntervalSeconds;
    }

    /// <summary>
    /// Base class for ghost lifecycle events fired by the engine.
    /// Policy subscribers receive these to make spawn/resource/camera decisions.
    /// </summary>
    internal class GhostLifecycleEvent
    {
        public int Index;
        public IPlaybackTrajectory Trajectory;
        public GhostPlaybackState State;
        public TrajectoryPlaybackFlags Flags;
    }

    /// <summary>
    /// Fired when a ghost's trajectory reaches its end (or chain end).
    /// Policy layer uses this to decide: spawn real vessel, apply resources, manage camera.
    /// </summary>
    internal class PlaybackCompletedEvent : GhostLifecycleEvent
    {
        /// <summary>Whether a ghost GameObject was active when playback ended.</summary>
        public bool GhostWasActive;

        /// <summary>Whether the effective end UT was exceeded (may extend beyond this segment's own EndUT).</summary>
        public bool PastEffectiveEnd;

        /// <summary>The final trajectory point (for spawn positioning).</summary>
        public TrajectoryPoint LastPoint;

        /// <summary>The current UT when playback completed.</summary>
        public double CurrentUT;
    }

    /// <summary>
    /// Fired when a looping ghost completes a cycle and restarts.
    /// </summary>
    internal class LoopRestartedEvent : GhostLifecycleEvent
    {
        public int PreviousCycleIndex;
        public int NewCycleIndex;
        public bool ExplosionFired;
        public Vector3 ExplosionPosition;
    }

    /// <summary>
    /// Fired when an overlap ghost expires (negative-interval loop).
    /// </summary>
    internal class OverlapExpiredEvent : GhostLifecycleEvent
    {
        public int CycleIndex;
        public bool ExplosionFired;
        public Vector3 ExplosionPosition;
    }

    /// <summary>
    /// Type of camera action the engine requests from the host.
    /// The engine detects the visual state change; the host handles camera manipulation.
    /// </summary>
    internal enum CameraActionType
    {
        /// <summary>Hold camera at explosion position (create anchor GO).</summary>
        ExplosionHoldStart,

        /// <summary>Explosion hold complete — ready for retarget.</summary>
        ExplosionHoldEnd,

        /// <summary>New loop cycle ghost spawned — retarget camera to it.</summary>
        RetargetToNewGhost,

        /// <summary>Ghost gone with no successor — exit watch mode.</summary>
        ExitWatch
    }

    /// <summary>
    /// Camera action event fired by the engine during loop/overlap cycle transitions.
    /// The engine provides the visual data; the host (ParsekFlight) manipulates FlightCamera.
    /// </summary>
    internal class CameraActionEvent
    {
        /// <summary>Recording index this action relates to.</summary>
        public int Index;

        /// <summary>Trajectory for logging and identification.</summary>
        public IPlaybackTrajectory Trajectory;

        /// <summary>Policy flags for identification.</summary>
        public TrajectoryPlaybackFlags Flags;

        /// <summary>What camera action is needed.</summary>
        public CameraActionType Action;

        /// <summary>Cycle index for RetargetToNewGhost.</summary>
        public int NewCycleIndex;

        /// <summary>World position for ExplosionHoldStart anchor.</summary>
        public Vector3 AnchorPosition;

        /// <summary>Ghost camera pivot transform for RetargetToNewGhost.</summary>
        public Transform GhostPivot;

        /// <summary>UT until which to hold the camera (for ExplosionHoldStart).</summary>
        public double HoldUntilUT;
    }
}
