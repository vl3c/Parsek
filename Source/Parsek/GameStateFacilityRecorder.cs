using System;
using System.Collections.Generic;
using System.Globalization;

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
                    bool intact;
                    if (db != null && !string.IsNullOrEmpty(db.id)
                        && TryReadSettledIntact(db.IsIntact, db.IsDestroyed, out intact))
                        lastBuildingIntact[db.id] = intact;
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

        /// <summary>
        /// GameEvents.OnKSCStructureCollapsing handler. Stock <c>DestructibleBuilding.Demolish()</c>
        /// (reached from <c>AddDamage</c> when a crash exceeds the building's toughness, and
        /// from the KSC context menu's debug Demolish) sets <c>intact = false</c> and fires
        /// this synchronously, once per building; <c>OnKSCStructureCollapsed</c> follows only
        /// after the collapse animation, from a coroutine a quit or scene change can cut short,
        /// so the collapse is recorded here, at the moment it happens.
        /// </summary>
        internal void OnStructureCollapsing(DestructibleBuilding db)
        {
            if (db == null)
                return;
            RecordBuildingTransition(db.id, false, Planetarium.GetUniversalTime(), 0f, "event-collapsing");
        }

        /// <summary>
        /// GameEvents.OnKSCStructureRepairing handler. Stock <c>DestructibleBuilding.Repair()</c>
        /// sets <c>destroyed = false</c> and fires this synchronously for each building that
        /// was destroyed; <c>OnKSCStructureRepaired</c> follows only after the repair
        /// animation. The funds share comes from the <see cref="FacilityRepairCapture"/> scope
        /// of the enclosing <c>SpaceCenterBuilding.RepairFacility</c> call (0 outside one).
        /// </summary>
        internal void OnStructureRepairing(DestructibleBuilding db)
        {
            if (db == null)
                return;
            RecordBuildingTransition(db.id, true, Planetarium.GetUniversalTime(),
                FacilityRepairCapture.CostForBuilding(db.id), "event-repairing");
        }

        /// <summary>
        /// <see cref="FacilityRepairCapture.StructuresReset"/> handler: buildings a facility
        /// upgrade / downgrade reset to intact without any repair event. Cost 0 - the funds
        /// are the StructureConstruction debit the FacilityUpgrade row carries.
        /// </summary>
        internal void OnStructuresReset(IList<string> buildingIds)
        {
            if (buildingIds == null)
                return;
            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < buildingIds.Count; i++)
                RecordBuildingTransition(buildingIds[i], true, ut, 0f, "facility-level-reset");
        }

        /// <summary>
        /// Pure: whether a building transition reported by stock should be recorded. False
        /// for an empty id, and while Parsek itself is driving the building (the recalc's
        /// <c>FacilityStatePatcher.PatchDestructionState</c> calls <c>Demolish()</c> /
        /// <c>Repair()</c> inside <c>SuppressionGuard.ResourcesAndReplay</c>, which fires the
        /// same events and must not read back as a player action).
        /// </summary>
        internal static bool ShouldRecordBuildingTransition(
            bool isReplayingActions, bool suppressResourceEvents, string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId))
                return false;
            return !isReplayingActions && !suppressResourceEvents;
        }

        /// <summary>
        /// Pure: the game-state event for a building transition. A repair carries its funds
        /// cost in <c>detail</c> (<c>cost=</c>, read by <c>ConvertBuildingRepaired</c>); a
        /// destruction carries none (stock charges nothing for a collapse).
        /// </summary>
        internal static GameStateEvent CreateBuildingEvent(
            string buildingId, bool nowIntact, double ut, float repairCost)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = nowIntact
                    ? GameStateEventType.BuildingRepaired
                    : GameStateEventType.BuildingDestroyed,
                key = buildingId,
                detail = nowIntact ? FacilityRepairCapture.BuildRepairDetail(repairCost) : null
            };
        }

        /// <summary>
        /// Records one building transition: emits the game-state event (tagged with the live
        /// recording, if any), keeps the poll cache coherent, and forwards an UNTAGGED event
        /// with no live recorder straight to the ledger - the same gate facility upgrades use
        /// (<see cref="GameStateRecorder.ShouldForwardFacilityLedgerEvent"/>). A tagged event
        /// becomes a ledger row when its recording commits (GameStateEventConverter). A repair
        /// inside a <c>RepairFacility</c> scope is queued and written with its sibling
        /// buildings in one batch when the scope closes.
        /// Returns true when an event was emitted.
        /// </summary>
        internal bool RecordBuildingTransition(
            string buildingId, bool nowIntact, double ut, float repairCost, string source)
        {
            if (!ShouldRecordBuildingTransition(
                    GameStateRecorder.IsReplayingActions,
                    GameStateRecorder.SuppressResourceEvents,
                    buildingId))
            {
                // Keep the cache on the patched state so a later poll does not report a
                // Parsek-driven Demolish / Repair as a player change.
                if (!string.IsNullOrEmpty(buildingId))
                    lastBuildingIntact[buildingId] = nowIntact;
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-building-transition",
                    $"Suppressed building transition '{buildingId ?? "(null)"}' intact={nowIntact} " +
                    $"source={source} (replay/resource suppression or empty id)", 5.0);
                return false;
            }

            var evt = CreateBuildingEvent(buildingId, nowIntact, ut, repairCost);
            owner.EmitFacilityEvent(ref evt, evt.eventType.ToString());
            lastBuildingIntact[buildingId] = nowIntact;
            ParsekLog.Info("GameStateRecorder",
                $"Game state: {evt.eventType} '{buildingId}' " +
                $"cost={(nowIntact ? repairCost : 0f).ToString("R", CultureInfo.InvariantCulture)} " +
                $"tag='{evt.recordingId ?? ""}' source={source}");

            if (!owner.ShouldForwardFacilityLedgerEvent(evt.recordingId))
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"Building transition '{buildingId}' owned by recording '{evt.recordingId ?? ""}' " +
                    "(or a live recorder) - becomes a ledger row at commit");
                return true;
            }

            if (nowIntact && FacilityRepairCapture.TryDeferForward(evt))
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"Building repair '{buildingId}' queued for the facility repair batch");
                return true;
            }

            LedgerOrchestrator.OnKscSpending(evt);
            return true;
        }

        /// <summary>
        /// Pure: the one intact test the cache seed, the poll and the event handlers share.
        /// A settled building is intact when stock's <c>IsIntact</c> is set. A building between
        /// states (<c>!IsIntact &amp;&amp; !IsDestroyed</c>: collapsing after <c>Demolish()</c>,
        /// or repairing after <c>Repair()</c>) has no settled value, returns false, and is
        /// neither seeded nor compared - its collapse / repair event already cached the state
        /// it is heading to, so a poll mid-animation cannot read a spurious transition.
        /// </summary>
        internal static bool TryReadSettledIntact(bool isIntact, bool isDestroyed, out bool intact)
        {
            intact = isIntact;
            return isIntact || isDestroyed;
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

            // Check building intact states. The event handlers below keep lastBuildingIntact
            // current, so a transition they already recorded is not reported twice here.
            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db == null || string.IsNullOrEmpty(db.id)) continue;

                    buildingsChecked++;
                    bool currentIntact;
                    if (!TryReadSettledIntact(db.IsIntact, db.IsDestroyed, out currentIntact))
                        continue; // mid-animation: its event already set the cache
                    bool cachedIntact;

                    if (lastBuildingIntact.TryGetValue(db.id, out cachedIntact)
                        && currentIntact != cachedIntact)
                    {
                        // A transition no event reported: record it the same way, at the
                        // poll's UT and with no known repair cost.
                        if (RecordBuildingTransition(db.id, currentIntact, ut, 0f, "poll"))
                            eventsEmitted++;
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
