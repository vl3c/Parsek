using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Facilities resource module. Tracks KSC facility upgrade levels and destruction/repair state.
    ///
    /// Implements the recalculation walk from design doc section 10:
    ///   - FacilityUpgrade: sets facility to the target level
    ///   - FacilityDestruction: marks facility as destroyed (level preserved for repair)
    ///   - FacilityRepair: clears destroyed flag, restoring facility to its pre-destruction level
    ///
    /// Facility upgrade and repair costs flow into the FundsModule as FundsSpending actions.
    /// This module does NOT handle cost accounting — it only tracks derived visual/level state.
    ///
    /// Registered at the Facilities tier in the RecalculationEngine, dispatched after
    /// first-tier, strategy transform, and second-tier modules.
    ///
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class FacilitiesModule : IResourceModule
    {
        private const string Tag = "Facilities";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Per-facility tracking state: level and destroyed flag.
        /// Level starts at 1 (default KSP facility level) for unknown facilities.
        /// </summary>
        internal struct FacilityState
        {
            /// <summary>Current upgrade level (1 = basic, 2 = upgraded, 3 = fully upgraded).</summary>
            public int Level;

            /// <summary>Whether the facility is currently destroyed.</summary>
            public bool Destroyed;
        }

        /// <summary>Per-facility state keyed by facilityId (e.g. "LaunchPad", "VehicleAssemblyBuilding").</summary>
        private readonly Dictionary<string, FacilityState> facilities = new Dictionary<string, FacilityState>();

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <summary>
        /// Resets all facility state before a recalculation walk.
        /// Clears all tracked facilities — they will be re-derived from actions.
        /// </summary>
        public void Reset()
        {
            int previousCount = facilities.Count;
            facilities.Clear();
            ParsekLog.Verbose(Tag, $"Reset: cleared {previousCount.ToString(IC)} tracked facilities");
        }

        /// <summary>
        /// Pre-pass: no-op for facilities module.
        /// Facility state is derived purely from the action walk — no aggregate information needed.
        /// </summary>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // No pre-pass needed for facilities; walkNowUT is ignored.
            return false;
        }

        /// <summary>
        /// Processes a single game action during the recalculation walk.
        /// Handles FacilityUpgrade, FacilityDestruction, and FacilityRepair;
        /// ignores all other action types.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            switch (action.Type)
            {
                case GameActionType.FacilityUpgrade:
                    ProcessUpgrade(action);
                    break;
                case GameActionType.FacilityDestruction:
                    ProcessDestruction(action);
                    break;
                case GameActionType.FacilityRepair:
                    ProcessRepair(action);
                    break;
                // All other action types: ignore silently
            }
        }

        // ================================================================
        // Action processing
        // ================================================================

        /// <summary>
        /// Processes FacilityUpgrade: sets the facility level to action.ToLevel.
        /// If the facility was destroyed, the upgrade still sets the new level
        /// (destruction state is preserved — the player must repair separately).
        /// </summary>
        private void ProcessUpgrade(GameAction action)
        {
            string facilityId = action.FacilityId ?? "";
            int toLevel = action.ToLevel;

            FacilityState state;
            if (!facilities.TryGetValue(facilityId, out state))
            {
                state = new FacilityState { Level = 1, Destroyed = false };
            }

            int previousLevel = state.Level;
            state.Level = toLevel;
            facilities[facilityId] = state;

            ParsekLog.Info(Tag,
                $"Upgrade: facility={facilityId}, " +
                $"fromLevel={previousLevel.ToString(IC)}, " +
                $"toLevel={toLevel.ToString(IC)}, " +
                $"destroyed={state.Destroyed.ToString(IC)}, " +
                $"UT={action.UT.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes FacilityDestruction: marks the facility as destroyed.
        /// The level is preserved so that repair restores the correct level.
        /// </summary>
        private void ProcessDestruction(GameAction action)
        {
            string facilityId = action.FacilityId ?? "";

            FacilityState state;
            if (!facilities.TryGetValue(facilityId, out state))
            {
                state = new FacilityState { Level = 1, Destroyed = false };
            }

            state.Destroyed = true;
            facilities[facilityId] = state;

            ParsekLog.Info(Tag,
                $"Destruction: facility={facilityId}, " +
                $"level={state.Level.ToString(IC)}, " +
                $"recordingId={action.RecordingId ?? "(none)"}, " +
                $"UT={action.UT.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes FacilityRepair: clears the destroyed flag, restoring the facility
        /// to its pre-destruction level. The level is not changed by repair.
        /// </summary>
        private void ProcessRepair(GameAction action)
        {
            string facilityId = action.FacilityId ?? "";

            FacilityState state;
            if (!facilities.TryGetValue(facilityId, out state))
            {
                state = new FacilityState { Level = 1, Destroyed = false };
            }

            bool wasDestroyed = state.Destroyed;
            state.Destroyed = false;
            facilities[facilityId] = state;

            ParsekLog.Info(Tag,
                $"Repair: facility={facilityId}, " +
                $"level={state.Level.ToString(IC)}, " +
                $"wasDestroyed={wasDestroyed.ToString(IC)}, " +
                $"UT={action.UT.ToString("R", IC)}");
        }

        // ================================================================
        // Query methods
        // ================================================================

        /// <summary>
        /// Returns the current level of a facility.
        /// Returns 1 (default KSP level) if the facility has not been seen in any action.
        /// </summary>
        internal int GetFacilityLevel(string facilityId)
        {
            if (facilityId == null)
                return 1;

            FacilityState state;
            if (facilities.TryGetValue(facilityId, out state))
                return state.Level;

            return 1;
        }

        /// <summary>
        /// Returns whether a facility is currently destroyed.
        /// Returns false if the facility has not been seen in any action.
        /// </summary>
        internal bool IsFacilityDestroyed(string facilityId)
        {
            if (facilityId == null)
                return false;

            FacilityState state;
            if (facilities.TryGetValue(facilityId, out state))
                return state.Destroyed;

            return false;
        }

        /// <summary>
        /// Returns the full state of a facility (level + destroyed flag).
        /// Returns default state (level=1, destroyed=false) if the facility has not been seen.
        /// </summary>
        internal FacilityState GetFacilityState(string facilityId)
        {
            if (facilityId == null)
                return new FacilityState { Level = 1, Destroyed = false };

            FacilityState state;
            if (facilities.TryGetValue(facilityId, out state))
                return state;

            return new FacilityState { Level = 1, Destroyed = false };
        }

        /// <summary>
        /// Returns a read-only view of all tracked facilities for patching KSP state on warp exit.
        /// The dictionary maps facilityId to current FacilityState.
        /// </summary>
        internal IReadOnlyDictionary<string, FacilityState> GetAllFacilities()
        {
            return facilities;
        }

        /// <summary>Returns the number of tracked facilities (for diagnostics).</summary>
        internal int FacilityCount => facilities.Count;

        public void PostWalk() { }
    }
}
