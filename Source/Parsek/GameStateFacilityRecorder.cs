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

        /// <summary>
        /// Event-driven facility-upgrade capture. The poll (<see cref="PollFacilityState"/>)
        /// only runs on scene load, so an upgrade applied WITHIN a scene (a stock UI upgrade,
        /// or the seam's SpaceCenterBuilding.UpgradeFacility) that is not followed by a scene
        /// change -- e.g. a seam run that upgrades then immediately quits -- would never be
        /// recorded. Worse, the poll baseline can seed empty on a cold no-vessel load
        /// (ScenarioUpgradeableFacilities.protoUpgradeables facilityRefs not populated yet),
        /// leaving no cached "before" level to diff against even if a later poll did run.
        ///
        /// KSP fires GameEvents.OnKSCFacilityUpgrading SYNCHRONOUSLY inside
        /// UpgradeableFacility.SetLevel (before the level actually changes) for BOTH the UI
        /// and the seam, so subscribing here records a seam upgrade identically to a UI one
        /// and needs no seeded baseline (the event carries before + after directly). We use
        /// Upgrading (synchronous) rather than OnKSCFacilityUpgraded, which fires from a
        /// spawn-gated coroutine that may never complete headlessly / before a quit.
        ///
        /// Replay guard: FacilityStatePatcher.SetLevel re-fires this same event during the
        /// recalc apply boundary (wrapped in SuppressionGuard.ResourcesAndReplay), so bail on
        /// IsReplayingActions / SuppressResourceEvents to never re-record a Parsek-driven
        /// level patch as a fresh player upgrade.
        /// </summary>
        internal void OnFacilityUpgrading(Upgradeables.UpgradeableFacility fac, int newLevelIndex)
        {
            if (GameStateRecorder.IsReplayingActions || GameStateRecorder.SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-facility-upgrading",
                    "Suppressed OnKSCFacilityUpgrading during replay/resource suppression", 5.0);
                return;
            }
            if (fac == null || string.IsNullOrEmpty(fac.id))
                return;

            // Upgrading fires before setLevel(lvl), so GetNormLevel() still reads the OLD
            // level. The new normalized level is derived from the target index + MaxLevel,
            // matching KSP's GetNormLevel contract (level / MaxLevel).
            float before = fac.GetNormLevel();
            float after = NormalizedLevel(newLevelIndex, fac.MaxLevel);

            GameStateEventType? kind = ClassifyFacilityLevelChange(before, after);
            if (kind == null)
            {
                // No-op SetLevel: keep the poll cache coherent so a later poll sees no delta.
                lastFacilityLevels[fac.id] = after;
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = kind.Value,
                key = fac.id,
                valueBefore = before,
                valueAfter = after
            };
            owner.EmitFacilityEvent(ref evt, kind.Value.ToString());
            ParsekLog.Info("GameStateRecorder",
                $"Game state: {kind.Value} '{fac.id}' {before:F2} → {after:F2} (event-driven)");

            // Mirror the poll's ledger-forward: only upgrades forward (downgrades are
            // informational), gated on ShouldForwardFacilityLedgerEvent.
            if (kind.Value == GameStateEventType.FacilityUpgraded
                && owner.ShouldForwardFacilityLedgerEvent(evt.recordingId))
                LedgerOrchestrator.OnKscSpending(evt);

            // Update the poll cache so a subsequent scene-change poll does not re-emit.
            lastFacilityLevels[fac.id] = after;
        }

        /// <summary>
        /// Pure decision for a facility level change: given the pre-change and post-change
        /// normalized levels, return the event type, or null when the delta is below the
        /// same 0.001 epsilon the poll uses (a no-op / rounding SetLevel).
        /// </summary>
        internal static GameStateEventType? ClassifyFacilityLevelChange(float beforeNorm, float afterNorm)
        {
            if (Math.Abs(afterNorm - beforeNorm) <= 0.001f)
                return null;
            return afterNorm > beforeNorm
                ? GameStateEventType.FacilityUpgraded
                : GameStateEventType.FacilityDowngraded;
        }

        /// <summary>
        /// Normalized facility level (KSP's GetNormLevel contract: zero-based level index
        /// divided by MaxLevel). Returns 0 for a non-positive MaxLevel (unloaded facility).
        /// </summary>
        internal static float NormalizedLevel(int levelIndex, int maxLevel)
        {
            return maxLevel > 0 ? (float)levelIndex / maxLevel : 0f;
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
