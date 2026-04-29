using System;
using System.Collections.Generic;

namespace Parsek
{
    internal sealed class GameStateFacilityRecorder
    {
        private readonly GameStateRecorder owner;

        // Cached facility/building state for polling on scene change.
        private readonly Dictionary<string, float> lastFacilityLevels = new Dictionary<string, float>();
        private readonly Dictionary<string, bool> lastBuildingIntact = new Dictionary<string, bool>();

        internal GameStateFacilityRecorder(GameStateRecorder owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        internal void SeedFacilityCacheFromCurrentState()
        {
            // Facility levels
            if (ScenarioUpgradeableFacilities.protoUpgradeables != null)
            {
                foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
                {
                    if (kvp.Value != null && kvp.Value.facilityRefs != null &&
                        kvp.Value.facilityRefs.Count > 0)
                    {
                        var facility = kvp.Value.facilityRefs[0];
                        if (facility != null)
                            lastFacilityLevels[kvp.Key] = facility.GetNormLevel();
                    }
                }
            }

            // Building intact states
            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db != null && !string.IsNullOrEmpty(db.id))
                        lastBuildingIntact[db.id] = !db.IsDestroyed;
                }
            }

            ParsekLog.Info("GameStateRecorder", $"Game state: Facility cache seeded from current state " +
                $"({lastFacilityLevels.Count} facilities, {lastBuildingIntact.Count} buildings)");
        }

        internal void PollFacilityState()
        {
            double ut = Planetarium.GetUniversalTime();
            int facilitiesChecked = 0;
            int buildingsChecked = 0;
            int eventsEmitted = 0;

            // Check facility levels
            if (ScenarioUpgradeableFacilities.protoUpgradeables != null)
            {
                foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
                {
                    if (kvp.Value == null || kvp.Value.facilityRefs == null ||
                        kvp.Value.facilityRefs.Count == 0) continue;

                    var facility = kvp.Value.facilityRefs[0];
                    if (facility == null) continue;

                    facilitiesChecked++;
                    float currentLevel = facility.GetNormLevel();
                    float cachedLevel;

                    if (lastFacilityLevels.TryGetValue(kvp.Key, out cachedLevel))
                    {
                        if (Math.Abs(currentLevel - cachedLevel) > 0.001f)
                        {
                            var eventType = currentLevel > cachedLevel
                                ? GameStateEventType.FacilityUpgraded
                                : GameStateEventType.FacilityDowngraded;

                            var evt = new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = kvp.Key,
                                valueBefore = cachedLevel,
                                valueAfter = currentLevel
                            };
                            owner.EmitFacilityEvent(ref evt, eventType.ToString());
                            eventsEmitted++;
                            ParsekLog.Info("GameStateRecorder", $"Game state: {eventType} '{kvp.Key}' {cachedLevel:F2} → {currentLevel:F2}");

                            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so
                            // untagged pre-recording FLIGHT facility upgrades reach the
                            // ledger too. Only FacilityUpgraded forwards (downgrades are
                            // informational).
                            if (eventType == GameStateEventType.FacilityUpgraded
                                && owner.ShouldForwardFacilityLedgerEvent(evt.recordingId))
                                LedgerOrchestrator.OnKscSpending(evt);
                        }
                    }

                    lastFacilityLevels[kvp.Key] = currentLevel;
                }
            }

            // Check building intact states
            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db == null || string.IsNullOrEmpty(db.id)) continue;

                    buildingsChecked++;
                    bool currentIntact = !db.IsDestroyed;
                    bool cachedIntact;

                    if (lastBuildingIntact.TryGetValue(db.id, out cachedIntact))
                    {
                        if (currentIntact != cachedIntact)
                        {
                            var eventType = currentIntact
                                ? GameStateEventType.BuildingRepaired
                                : GameStateEventType.BuildingDestroyed;

                            var bldEvt = new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = db.id
                            };
                            owner.EmitFacilityEvent(ref bldEvt, eventType.ToString());
                            eventsEmitted++;
                            ParsekLog.Info("GameStateRecorder", $"Game state: {eventType} '{db.id}'");
                        }
                    }

                    lastBuildingIntact[db.id] = currentIntact;
                }
            }

            ParsekLog.Verbose("GameStateRecorder",
                $"Facility poll pass: facilitiesChecked={facilitiesChecked}, buildingsChecked={buildingsChecked}, " +
                $"eventsEmitted={eventsEmitted}");
        }

        internal static List<GameStateEvent> CheckFacilityTransitions(
            Dictionary<string, float> cached, Dictionary<string, float> current, double ut)
        {
            var result = new List<GameStateEvent>();

            foreach (var kvp in current)
            {
                float cachedLevel;
                if (cached.TryGetValue(kvp.Key, out cachedLevel))
                {
                    if (Math.Abs(kvp.Value - cachedLevel) > 0.001f)
                    {
                        result.Add(new GameStateEvent
                        {
                            ut = ut,
                            eventType = kvp.Value > cachedLevel
                                ? GameStateEventType.FacilityUpgraded
                                : GameStateEventType.FacilityDowngraded,
                            key = kvp.Key,
                            valueBefore = cachedLevel,
                            valueAfter = kvp.Value
                        });
                    }
                }
            }

            return result;
        }

        internal static List<GameStateEvent> CheckBuildingTransitions(
            Dictionary<string, bool> cached, Dictionary<string, bool> current, double ut)
        {
            var result = new List<GameStateEvent>();

            foreach (var kvp in current)
            {
                bool cachedIntact;
                if (cached.TryGetValue(kvp.Key, out cachedIntact))
                {
                    if (kvp.Value != cachedIntact)
                    {
                        result.Add(new GameStateEvent
                        {
                            ut = ut,
                            eventType = kvp.Value
                                ? GameStateEventType.BuildingRepaired
                                : GameStateEventType.BuildingDestroyed,
                            key = kvp.Key
                        });
                    }
                }
            }

            return result;
        }
    }
}
