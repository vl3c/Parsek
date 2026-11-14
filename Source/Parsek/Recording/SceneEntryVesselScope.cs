using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The scene-entry vessel scope (RecordingStore services plan, service F): the active and
    /// fresh-rollout vessel the scene started with, and the launch-point rewind's replay
    /// target. The #226 replay bypass and the spawn dedup read these; they are save-scoped,
    /// so a cold load forgets them (<see cref="ResetForLoad"/>).
    /// </summary>
    internal static class SceneEntryVesselScope
    {
        // The save-scoped state: every field below. The Cold reset replaces the whole object
        // (ResetForLoad), so a field added here is forgotten with the rest.
        private sealed class ColdResetState
        {
            internal uint SceneEntryActiveVesselPid;
            internal string SceneEntryActiveVesselGuid;
            internal uint SceneEntryFreshRolloutVesselPid;
            internal uint RewindReplayTargetSourcePid;
            internal string RewindReplayTargetRecordingId;
        }

        private static ColdResetState state = new ColdResetState();

        // PID of the active vessel at scene entry. Used by SpawnVesselOrChainTip to
        // bypass PID dedup statelessly - if a recording's VesselPersistentId matches
        // this, the existing real vessel is the player's reverted/active vessel, not
        // a previously-spawned endpoint. Static so it survives Recording object recreation.
        internal static uint SceneEntryActiveVesselPid { get => state.SceneEntryActiveVesselPid; set => state.SceneEntryActiveVesselPid = value; }

        // Launch guid ("N" form, null = unknown) of that scene-entry vessel. The #226 bypass
        // is withheld when no pid-matching live vessel is the recording's launch
        // (VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity).
        internal static string SceneEntryActiveVesselGuid { get => state.SceneEntryActiveVesselGuid; set => state.SceneEntryActiveVesselGuid = value; }

        // PID of the vessel that rolled out fresh from the VAB/SPH this scene
        // (set only for NEW_FROM_FILE / NEW_FROM_CRAFT_NODE startups; 0 otherwise).
        // Read by CrewReservationManager.SwapReservedCrewInFlight to suppress Pass-2
        // orphan crew placement when the active vessel is this fresh launch - a new
        // mission has no orphaned reserved crew to reclaim, and reclaiming would
        // mis-seat stand-ins via KSP's craft-stable part persistentId reuse. Static
        // so the chain-commit / merge call sites (which have no ParsekFlight handle)
        // can consult it, mirroring SceneEntryActiveVesselPid.
        internal static uint SceneEntryFreshRolloutVesselPid { get => state.SceneEntryFreshRolloutVesselPid; set => state.SceneEntryFreshRolloutVesselPid = value; }

        // During launch-point rewind, scope the #226 duplicate-source bypass to the
        // recording whose rewind was actually requested. Other scene-entry vessels may
        // also match committed recordings, but they are not the replay target.
        internal static uint RewindReplayTargetSourcePid { get => state.RewindReplayTargetSourcePid; set => state.RewindReplayTargetSourcePid = value; }
        internal static string RewindReplayTargetRecordingId { get => state.RewindReplayTargetRecordingId; set => state.RewindReplayTargetRecordingId = value; }

        /// <summary>Cold-load reset, called by <c>RecordingStore.ResetSaveScopedStateForLoad</c>
        /// at the point where it used to clear these fields itself.</summary>
        internal static void ResetForLoad()
        {
            state = new ColdResetState();
        }

        /// <summary>Test reset, called by <c>RecordingStore.ResetForTestingInternal</c> at the
        /// point where it used to clear these fields itself.</summary>
        internal static void ResetForTesting()
        {
            state = new ColdResetState();
        }

        internal static void SetRewindReplayTargetScope(Recording owner)
        {
            // NOTE: This scope must survive the committed-list rebuild inside
            // ParsekScenario.OnLoad; only real commit/discard/clear/reset paths
            // should clear it, or #565's replay-target filter loses its owner.
            RewindReplayTargetSourcePid = owner != null ? owner.VesselPersistentId : 0u;
            RewindReplayTargetRecordingId = owner != null ? owner.RecordingId : null;
            if (RewindReplayTargetSourcePid != 0)
            {
                ParsekLog.Verbose("Rewind",
                    $"Rewind replay duplicate scope armed: rec={RewindReplayTargetRecordingId ?? "<null>"} " +
                    $"sourcePid={RewindReplayTargetSourcePid.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        internal static void ClearRewindReplayTargetScope()
        {
            RewindReplayTargetSourcePid = 0;
            RewindReplayTargetRecordingId = null;
        }
    }
}
