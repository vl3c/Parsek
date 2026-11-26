using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// What a promotion needs to know about one live part to say what the promoted recording's
    /// ghost shows for it: its pid, the pose the ghost spawns it at, and which tracking keys its
    /// modules own. Built from the live vessel by
    /// <see cref="PartStateSeeder.CollectPromotionPartFacts"/>.
    /// </summary>
    internal sealed class PromotionPartFacts
    {
        internal uint pid;
        internal string partName;

        /// <summary>
        /// The recording already has a start ghost snapshot and it does not hold this part, so
        /// the ghost does not draw it and has no spawn pose for it.
        /// </summary>
        internal bool absentFromStartSnapshot;

        /// <summary>
        /// The part's state in the recording's START ghost snapshot. Null when the snapshot is
        /// not captured yet: the recorder captures it later in the same start, from the vessel as
        /// it is now, so the ghost will spawn as the live part.
        /// </summary>
        internal SnapshotPartBaseline spawn;

        /// <summary>The start snapshot shows the part's shroud jettisoned (GhostVisualBuilder
        /// hides the jettison transforms at build).</summary>
        internal bool spawnShroudJettisoned;

        /// <summary>The start snapshot shows the part's procedural fairing deployed
        /// (GhostVisualBuilder.Fairings builds no shell).</summary>
        internal bool spawnFairingDeployed;

        /// <summary>A parachute the snapshot reader does not read: an EVA kerbal's
        /// ModuleEvaChute (the reader matches ModuleParachute by name).</summary>
        internal bool chuteOutsideSnapshotReader;

        /// <summary>The part's ModuleDeployablePart is one the snapshot reader does not read: a
        /// robot arm scanner.</summary>
        internal bool deployableOutsideSnapshotReader;

        internal bool hasChute;

        /// <summary>A ModuleDeployablePart of any kind (its broken state is tracked).</summary>
        internal bool hasDeployable;

        /// <summary>The ModuleDeployablePart has a pose animation, so the seeder tracks its
        /// extended state (<see cref="PartStateSeeder.DeployableHasNoPoseAnimation"/>).</summary>
        internal bool hasPosedDeployable;

        internal bool hasGear;
        internal bool hasTrackedCargoBay;

        /// <summary>A lamp the seeder and the snapshot reader read from the same module.</summary>
        internal bool hasSnapshotReadableLight;

        internal bool hasWheelDamage;
        internal bool hasShroud;
        internal bool hasFairing;

        // The module of each per-module family the snapshot reader has an opinion on, when its
        // live state is definite; -1 otherwise (no such module, or mid-travel / unclassified).
        internal int ladderIndex = -1;
        internal int animationGroupIndex = -1;
        internal int animateGenericIndex = -1;
        internal int aeroSurfaceIndex = -1;
        internal int controlSurfaceIndex = -1;

        /// <summary>Every module index tracked per module, by family, so a recorded
        /// DeployableExtended / Retracted is read back into the set it came from.</summary>
        internal readonly Dictionary<int, PromotionModuleFamily> moduleFamilies =
            new Dictionary<int, PromotionModuleFamily>();
    }

    /// <summary>The families whose deploy state is tracked per module.</summary>
    internal enum PromotionModuleFamily
    {
        Ladder,
        AnimationGroup,
        AnimateGeneric,
        AeroSurface,
        ControlSurface,
        RobotArmScanner,
    }
}
