using System;
using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Records career-mode game state changes (contracts, tech, crew, facilities, resources).
    /// Lifecycle managed by ParsekScenario — Subscribe/Unsubscribe called on every OnLoad.
    /// </summary>
    internal class GameStateRecorder
    {
        /// <summary>
        /// Set to true by ParsekScenario during its own crew mutations
        /// (ReserveCrewIn, UnreserveCrewInSnapshot, CleanUpReplacement, ClearReplacements)
        /// to prevent recording Parsek's internal bookkeeping as real game state events.
        /// </summary>
        internal static bool SuppressCrewEvents = false;

        private bool subscribed = false;

        // Cached facility/building state for polling on scene change
        private Dictionary<string, float> lastFacilityLevels = new Dictionary<string, float>();
        private Dictionary<string, bool> lastBuildingIntact = new Dictionary<string, bool>();

        // Resource tracking for threshold checks
        private double lastFunds = double.NaN;
        private double lastScience = double.NaN;
        private float lastReputation = float.NaN;

        private const double FundsThreshold = 100.0;
        private const double ScienceThreshold = 1.0;
        private const float ReputationThreshold = 1.0f;

        #region Subscription Management

        internal void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;

            // Contracts
            GameEvents.Contract.onOffered.Add(OnContractOffered);
            GameEvents.Contract.onAccepted.Add(OnContractAccepted);
            GameEvents.Contract.onCompleted.Add(OnContractCompleted);
            GameEvents.Contract.onFailed.Add(OnContractFailed);
            GameEvents.Contract.onCancelled.Add(OnContractCancelled);
            GameEvents.Contract.onDeclined.Add(OnContractDeclined);

            // Tech
            GameEvents.OnTechnologyResearched.Add(OnTechResearched);
            GameEvents.OnPartPurchased.Add(OnPartPurchased);

            // Crew
            GameEvents.onKerbalAdded.Add(OnKerbalAdded);
            GameEvents.onKerbalRemoved.Add(OnKerbalRemoved);
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatusChange);

            // Resources
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnScienceChanged.Add(OnScienceChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);

            // Initialize resource tracking from current state
            SeedResourceState();

            // Poll facility/building state for changes since last save
            PollFacilityState();

            ParsekLog.Log($"GameStateRecorder subscribed ({GameStateStore.EventCount} events in history)");
        }

        internal void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;

            // Contracts
            GameEvents.Contract.onOffered.Remove(OnContractOffered);
            GameEvents.Contract.onAccepted.Remove(OnContractAccepted);
            GameEvents.Contract.onCompleted.Remove(OnContractCompleted);
            GameEvents.Contract.onFailed.Remove(OnContractFailed);
            GameEvents.Contract.onCancelled.Remove(OnContractCancelled);
            GameEvents.Contract.onDeclined.Remove(OnContractDeclined);

            // Tech
            GameEvents.OnTechnologyResearched.Remove(OnTechResearched);
            GameEvents.OnPartPurchased.Remove(OnPartPurchased);

            // Crew
            GameEvents.onKerbalAdded.Remove(OnKerbalAdded);
            GameEvents.onKerbalRemoved.Remove(OnKerbalRemoved);
            GameEvents.onKerbalStatusChange.Remove(OnKerbalStatusChange);

            // Resources
            GameEvents.OnFundsChanged.Remove(OnFundsChanged);
            GameEvents.OnScienceChanged.Remove(OnScienceChanged);
            GameEvents.OnReputationChanged.Remove(OnReputationChanged);

            ParsekLog.Log("GameStateRecorder unsubscribed");
        }

        #endregion

        #region Contract Handlers

        private void OnContractOffered(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractOffered,
                key = contract.ContractGuid.ToString(),
                detail = title
            });
            ParsekLog.Log($"Game state: ContractOffered '{title}'");
        }

        private void OnContractAccepted(Contract contract)
        {
            if (contract == null) return;
            string guid = contract.ContractGuid.ToString();

            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractAccepted,
                key = guid,
                detail = title
            });

            // Store full contract snapshot for reversal
            try
            {
                var contractNode = new ConfigNode("CONTRACT");
                contract.Save(contractNode);
                GameStateStore.AddContractSnapshot(guid, contractNode);
                ParsekLog.Log($"Game state: ContractAccepted '{title}' (snapshot saved)");
            }
            catch (Exception ex)
            {
                ParsekLog.Log($"Game state: ContractAccepted '{title}' (snapshot FAILED: {ex.Message})");
            }
        }

        private void OnContractCompleted(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCompleted,
                key = contract.ContractGuid.ToString(),
                detail = title
            });
            ParsekLog.Log($"Game state: ContractCompleted '{title}'");
        }

        private void OnContractFailed(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractFailed,
                key = contract.ContractGuid.ToString(),
                detail = title
            });
            ParsekLog.Log($"Game state: ContractFailed '{title}'");
        }

        private void OnContractCancelled(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCancelled,
                key = contract.ContractGuid.ToString(),
                detail = title
            });
            ParsekLog.Log($"Game state: ContractCancelled '{title}'");
        }

        private void OnContractDeclined(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractDeclined,
                key = contract.ContractGuid.ToString(),
                detail = title
            });
            ParsekLog.Log($"Game state: ContractDeclined '{title}'");
        }

        #endregion

        #region Tech Handlers

        private void OnTechResearched(GameEvents.HostTargetAction<RDTech, RDTech.OperationResult> data)
        {
            if (data.host == null) return;
            if (data.target != RDTech.OperationResult.Successful) return;

            string techId = data.host.techID ?? "";
            string partList = "";
            if (data.host.partsAssigned != null && data.host.partsAssigned.Count > 0)
            {
                var names = new List<string>();
                foreach (var part in data.host.partsAssigned)
                {
                    if (part != null)
                        names.Add(part.name ?? "");
                }
                partList = string.Join(",", names);
            }

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.TechResearched,
                key = techId,
                detail = $"cost={data.host.scienceCost};parts={partList}",
                valueBefore = ResearchAndDevelopment.Instance != null
                    ? ResearchAndDevelopment.Instance.Science + data.host.scienceCost
                    : 0,
                valueAfter = ResearchAndDevelopment.Instance != null
                    ? ResearchAndDevelopment.Instance.Science
                    : 0
            });
            ParsekLog.Log($"Game state: TechResearched '{techId}' (cost={data.host.scienceCost})");
        }

        private void OnPartPurchased(AvailablePart part)
        {
            if (part == null) return;
            var partName = part.name ?? "";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.PartPurchased,
                key = partName,
                detail = $"cost={part.cost}",
                valueBefore = Funding.Instance != null ? Funding.Instance.Funds + part.cost : 0,
                valueAfter = Funding.Instance != null ? Funding.Instance.Funds : 0
            });
            ParsekLog.Log($"Game state: PartPurchased '{partName}' (cost={part.cost})");
        }

        #endregion

        #region Crew Handlers

        private void OnKerbalAdded(ProtoCrewMember crew)
        {
            if (SuppressCrewEvents || crew == null) return;
            var name = crew.name ?? "";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewHired,
                key = name,
                detail = $"trait={crew.trait ?? ""}"
            });
            ParsekLog.Log($"Game state: CrewHired '{name}' ({crew.trait ?? "?"})");
        }

        private void OnKerbalRemoved(ProtoCrewMember crew)
        {
            if (SuppressCrewEvents || crew == null) return;
            var name = crew.name ?? "";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewRemoved,
                key = name,
                detail = $"trait={crew.trait ?? ""}"
            });
            ParsekLog.Log($"Game state: CrewRemoved '{name}'");
        }

        private void OnKerbalStatusChange(ProtoCrewMember crew,
            ProtoCrewMember.RosterStatus oldStatus, ProtoCrewMember.RosterStatus newStatus)
        {
            if (SuppressCrewEvents || crew == null) return;
            var name = crew.name ?? "";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewStatusChanged,
                key = name,
                detail = $"from={oldStatus};to={newStatus}"
            });
            ParsekLog.Log($"Game state: CrewStatusChanged '{name}' {oldStatus} → {newStatus}");
        }

        #endregion

        #region Resource Handlers

        private void SeedResourceState()
        {
            if (Funding.Instance != null)
                lastFunds = Funding.Instance.Funds;
            if (ResearchAndDevelopment.Instance != null)
                lastScience = ResearchAndDevelopment.Instance.Science;
            if (Reputation.Instance != null)
                lastReputation = Reputation.Instance.reputation;
        }

        private void OnFundsChanged(double newFunds, TransactionReasons reason)
        {
            double oldFunds = lastFunds;
            lastFunds = newFunds;

            if (double.IsNaN(oldFunds)) return;
            double delta = newFunds - oldFunds;
            if (Math.Abs(delta) < FundsThreshold) return;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.FundsChanged,
                key = reason.ToString(),
                valueBefore = oldFunds,
                valueAfter = newFunds
            });
            ParsekLog.Log($"Game state: FundsChanged {delta:+0;-0} ({reason}) → {newFunds:F0}");
        }

        private void OnScienceChanged(float newScience, TransactionReasons reason)
        {
            double oldScience = lastScience;
            lastScience = newScience;

            if (double.IsNaN(oldScience)) return;
            double delta = newScience - oldScience;
            if (Math.Abs(delta) < ScienceThreshold) return;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ScienceChanged,
                key = reason.ToString(),
                valueBefore = oldScience,
                valueAfter = newScience
            });
            ParsekLog.Log($"Game state: ScienceChanged {delta:+0.0;-0.0} ({reason}) → {newScience:F1}");
        }

        private void OnReputationChanged(float newReputation, TransactionReasons reason)
        {
            float oldReputation = lastReputation;
            lastReputation = newReputation;

            if (float.IsNaN(oldReputation)) return;
            float delta = newReputation - oldReputation;
            if (Math.Abs(delta) < ReputationThreshold) return;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ReputationChanged,
                key = reason.ToString(),
                valueBefore = oldReputation,
                valueAfter = newReputation
            });
            ParsekLog.Log($"Game state: ReputationChanged {delta:+0.0;-0.0} ({reason}) → {newReputation:F1}");
        }

        #endregion

        #region Facility Polling

        /// <summary>
        /// Seeds the facility/building cache from the most recent events in loaded history.
        /// Call after LoadEventFile() but before Subscribe()/PollFacilityState().
        /// </summary>
        internal void SeedFacilityCacheFromHistory()
        {
            lastFacilityLevels.Clear();
            lastBuildingIntact.Clear();

            var events = GameStateStore.Events;
            if (events == null || events.Count == 0)
            {
                SeedFacilityCacheFromCurrentState();
                return;
            }

            // Scan backward to find most recent state per facility/building
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                switch (e.eventType)
                {
                    case GameStateEventType.FacilityUpgraded:
                    case GameStateEventType.FacilityDowngraded:
                        if (!string.IsNullOrEmpty(e.key) && !lastFacilityLevels.ContainsKey(e.key))
                            lastFacilityLevels[e.key] = (float)e.valueAfter;
                        break;
                    case GameStateEventType.BuildingDestroyed:
                        if (!string.IsNullOrEmpty(e.key) && !lastBuildingIntact.ContainsKey(e.key))
                            lastBuildingIntact[e.key] = false;
                        break;
                    case GameStateEventType.BuildingRepaired:
                        if (!string.IsNullOrEmpty(e.key) && !lastBuildingIntact.ContainsKey(e.key))
                            lastBuildingIntact[e.key] = true;
                        break;
                }
            }

            // Fill in any facilities/buildings not found in history from current state
            SeedMissingFromCurrentState();

            ParsekLog.Log($"Game state: Facility cache seeded from history " +
                $"({lastFacilityLevels.Count} facilities, {lastBuildingIntact.Count} buildings)");
        }

        private void SeedFacilityCacheFromCurrentState()
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
        }

        private void SeedMissingFromCurrentState()
        {
            // Fill in facilities not found in event history
            if (ScenarioUpgradeableFacilities.protoUpgradeables != null)
            {
                foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
                {
                    if (!lastFacilityLevels.ContainsKey(kvp.Key) &&
                        kvp.Value != null && kvp.Value.facilityRefs != null &&
                        kvp.Value.facilityRefs.Count > 0)
                    {
                        var facility = kvp.Value.facilityRefs[0];
                        if (facility != null)
                            lastFacilityLevels[kvp.Key] = facility.GetNormLevel();
                    }
                }
            }

            // Fill in buildings not found in event history
            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db != null && !string.IsNullOrEmpty(db.id) &&
                        !lastBuildingIntact.ContainsKey(db.id))
                    {
                        lastBuildingIntact[db.id] = !db.IsDestroyed;
                    }
                }
            }
        }

        /// <summary>
        /// Polls current facility/building state and emits events for any changes
        /// since the cached state. Called on Subscribe() after cache is seeded.
        /// </summary>
        internal void PollFacilityState()
        {
            double ut = Planetarium.GetUniversalTime();

            // Check facility levels
            if (ScenarioUpgradeableFacilities.protoUpgradeables != null)
            {
                foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
                {
                    if (kvp.Value == null || kvp.Value.facilityRefs == null ||
                        kvp.Value.facilityRefs.Count == 0) continue;

                    var facility = kvp.Value.facilityRefs[0];
                    if (facility == null) continue;

                    float currentLevel = facility.GetNormLevel();
                    float cachedLevel;

                    if (lastFacilityLevels.TryGetValue(kvp.Key, out cachedLevel))
                    {
                        if (Math.Abs(currentLevel - cachedLevel) > 0.001f)
                        {
                            var eventType = currentLevel > cachedLevel
                                ? GameStateEventType.FacilityUpgraded
                                : GameStateEventType.FacilityDowngraded;

                            GameStateStore.AddEvent(new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = kvp.Key,
                                valueBefore = cachedLevel,
                                valueAfter = currentLevel
                            });
                            ParsekLog.Log($"Game state: {eventType} '{kvp.Key}' {cachedLevel:F2} → {currentLevel:F2}");
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

                    bool currentIntact = !db.IsDestroyed;
                    bool cachedIntact;

                    if (lastBuildingIntact.TryGetValue(db.id, out cachedIntact))
                    {
                        if (currentIntact != cachedIntact)
                        {
                            var eventType = currentIntact
                                ? GameStateEventType.BuildingRepaired
                                : GameStateEventType.BuildingDestroyed;

                            GameStateStore.AddEvent(new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = db.id
                            });
                            ParsekLog.Log($"Game state: {eventType} '{db.id}'");
                        }
                    }

                    lastBuildingIntact[db.id] = currentIntact;
                }
            }
        }

        #endregion

        #region Testing Support

        /// <summary>
        /// Checks the facility transition logic without Unity dependencies.
        /// Returns events that would be emitted for the given state change.
        /// </summary>
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

        /// <summary>
        /// Checks the building transition logic without Unity dependencies.
        /// Returns events that would be emitted for the given state change.
        /// </summary>
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

        #endregion
    }
}
