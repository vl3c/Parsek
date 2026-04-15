using System;
using System.Collections.Generic;
using System.Reflection;
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
        /// (UnreserveCrewInSnapshot, CleanUpReplacement, ClearReplacements)
        /// to prevent recording Parsek's internal bookkeeping as real game state events.
        /// </summary>
        internal static bool SuppressCrewEvents = false;

        /// <summary>
        /// Set to true by ParsekFlight during timeline resource replay
        /// (AddFunds/AddScience/AddReputation) to prevent recording replay
        /// mechanics as real game state events.
        /// </summary>
        internal static bool SuppressResourceEvents = false;

        /// <summary>
        /// Set to true by KspStatePatcher during ledger-based state patching to prevent
        /// recording replayed actions as new game state events and to bypass
        /// blocking Harmony patches (TechResearchPatch, FacilityUpgradePatch)
        /// that normally prevent duplicate actions on committed items.
        /// </summary>
        internal static bool IsReplayingActions = false;

        /// <summary>
        /// Science subjects captured during the current recording session.
        /// Transferred to GameStateStore.committedScienceSubjects on commit.
        /// </summary>
        internal static List<PendingScienceSubject> PendingScienceSubjects = new List<PendingScienceSubject>();

        private bool subscribed = false;

        // Cached facility/building state for polling on scene change
        private Dictionary<string, float> lastFacilityLevels = new Dictionary<string, float>();
        private Dictionary<string, bool> lastBuildingIntact = new Dictionary<string, bool>();

        // Crew status debouncing (filters EVA start/board bounce: Assigned→Available→Assigned)
        private struct PendingCrewEvent
        {
            public GameStateEvent gameEvent;
            public ProtoCrewMember.RosterStatus from;
            public ProtoCrewMember.RosterStatus to;
        }
        private Dictionary<string, PendingCrewEvent> pendingCrewEvents = new Dictionary<string, PendingCrewEvent>();

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
            pendingCrewEvents.Clear();

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
            GameEvents.onKerbalTypeChange.Add(OnKerbalTypeChange);

            // Resources
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnScienceChanged.Add(OnScienceChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);

            // Science subjects (per-experiment tracking for duplication prevention)
            GameEvents.OnScienceRecieved.Add(OnScienceReceived);

            // Progress milestones
            GameEvents.OnProgressComplete.Add(OnProgressComplete);

            // Initialize resource tracking from current state
            SeedResourceState();

            // Poll facility/building state for changes since last save
            PollFacilityState();

            ParsekLog.Info("GameStateRecorder", $"GameStateRecorder subscribed ({GameStateStore.EventCount} events in history)");
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
            GameEvents.onKerbalTypeChange.Remove(OnKerbalTypeChange);

            // Resources
            GameEvents.OnFundsChanged.Remove(OnFundsChanged);
            GameEvents.OnScienceChanged.Remove(OnScienceChanged);
            GameEvents.OnReputationChanged.Remove(OnReputationChanged);

            // Science subjects
            GameEvents.OnScienceRecieved.Remove(OnScienceReceived);

            // Progress milestones
            GameEvents.OnProgressComplete.Remove(OnProgressComplete);

            ParsekLog.Info("GameStateRecorder", "GameStateRecorder unsubscribed");
        }

        #endregion

        #region Contract Handlers

        private void OnContractOffered(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            // #398: deliberately do NOT add to GameStateStore. Offered contracts are
            // transient advertisements generated by KSP's ContractSystem tick — they are
            // not a player action, and every generation cycle creates fresh GUIDs that
            // never dedup. The converter already drops ContractOffered, and no UI or
            // module reads Offered events from the store. Keep the subscription for the
            // diagnostic log only.
            ParsekLog.Verbose("GameStateRecorder",
                $"Game state: ContractOffered '{title}' (diagnostic, not stored)");
        }

        private void OnContractAccepted(Contract contract)
        {
            if (contract == null) return;
            string guid = contract.ContractGuid.ToString();

            var title = contract.Title ?? "";
            double deadline = contract.TimeDeadline;
            float failFunds = (float)contract.FundsFailure;
            float failRep = (float)contract.ReputationFailure;

            // Structured detail: title + deadline + failure penalties for deadline expiration generation
            // deadline=0 means no deadline (KSP convention) — store as NaN
            string deadlineStr = deadline > 0
                ? deadline.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "NaN";
            var detail = $"title={title};deadline={deadlineStr}" +
                $";failFunds={failFunds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";failRep={failRep.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractAccepted,
                key = guid,
                detail = detail
            };
            GameStateStore.AddEvent(evt);

            // Store full contract snapshot for reversal
            try
            {
                var contractNode = new ConfigNode("CONTRACT");
                contract.Save(contractNode);
                GameStateStore.AddContractSnapshot(guid, contractNode);
                ParsekLog.Info("GameStateRecorder",
                    $"Game state: ContractAccepted '{title}' deadline={deadlineStr} " +
                    $"failFunds={failFunds} failRep={failRep} (snapshot saved)");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"Game state: ContractAccepted '{title}' (snapshot FAILED: {ex.Message})");
            }

            // Write directly to ledger when at KSC (not during a flight recording).
            // Flight-scene contracts flow through the normal commit-time ConvertEvents path.
            // #405: without this, accepted contracts at KSC never reached the ledger and
            // PatchContracts had no active contracts to preserve on next recalc.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractCompleted(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            var detail = $"title={title};fundsReward={contract.FundsCompletion};repReward={contract.ReputationCompletion};sciReward={contract.ScienceCompletion}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCompleted,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: ContractCompleted '{title}' (funds={contract.FundsCompletion}, rep={contract.ReputationCompletion}, sci={contract.ScienceCompletion})");

            // #405: route to ledger immediately when at KSC (contract completions in flight
            // go through the normal commit-time ConvertEvents path).
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractFailed(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            var detail = $"title={title};fundsPenalty={contract.FundsFailure};repPenalty={contract.ReputationFailure}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractFailed,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: ContractFailed '{title}' (fundsPenalty={contract.FundsFailure}, repPenalty={contract.ReputationFailure})");

            // #405: route to ledger immediately when at KSC.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractCancelled(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            var detail = $"title={title};fundsPenalty={contract.FundsFailure};repPenalty={contract.ReputationFailure}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCancelled,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: ContractCancelled '{title}' (fundsPenalty={contract.FundsFailure}, repPenalty={contract.ReputationFailure})");

            // #405: route to ledger immediately when at KSC.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
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
            ParsekLog.Info("GameStateRecorder", $"Game state: ContractDeclined '{title}'");
        }

        #endregion

        #region Tech Handlers

        private void OnTechResearched(GameEvents.HostTargetAction<RDTech, RDTech.OperationResult> data)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed TechResearched event during action replay");
                return;
            }
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

            var evt = new GameStateEvent
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
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: TechResearched '{techId}' (cost={data.host.scienceCost})");

            // Write directly to ledger when at KSC (not during flight recording)
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnPartPurchased(AvailablePart part)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed PartPurchased event during action replay");
                return;
            }
            if (part == null) return;
            var partName = part.name ?? "";

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.PartPurchased,
                key = partName,
                detail = $"cost={part.cost}",
                valueBefore = Funding.Instance != null ? Funding.Instance.Funds + part.cost : 0,
                valueAfter = Funding.Instance != null ? Funding.Instance.Funds : 0
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: PartPurchased '{partName}' (cost={part.cost})");

            // #405: route to ledger immediately when at KSC. Relies on the DedupKey (§F)
            // to disambiguate part-name collisions.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        #endregion

        #region Crew Handlers

        private void OnKerbalAdded(ProtoCrewMember crew)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed CrewHired event during action replay");
                return;
            }
            if (SuppressCrewEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-crew-added",
                    "Suppressed CrewHired event while Parsek mutates crew state", 5.0);
                return;
            }
            if (crew == null) return;
            var name = crew.name ?? "";

            // Capture hire cost from GameVariables if available (career mode)
            float hireCost = 0f;
            if (GameVariables.Instance != null && HighLogic.CurrentGame != null)
            {
                hireCost = (float)GameVariables.Instance.GetRecruitHireCost(
                    HighLogic.CurrentGame.CrewRoster.GetActiveCrewCount());
            }

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewHired,
                key = name,
                detail = $"trait={crew.trait ?? ""};cost={hireCost}"
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: CrewHired '{name}' ({crew.trait ?? "?"})");

            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnKerbalRemoved(ProtoCrewMember crew)
        {
            if (SuppressCrewEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-crew-removed",
                    "Suppressed CrewRemoved event while Parsek mutates crew state", 5.0);
                return;
            }
            if (crew == null) return;
            var name = crew.name ?? "";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewRemoved,
                key = name,
                detail = $"trait={crew.trait ?? ""}"
            });
            ParsekLog.Info("GameStateRecorder", $"Game state: CrewRemoved '{name}'");
        }

        /// <summary>
        /// Returns true if we're currently in the Flight scene. KSC spending actions
        /// should only be written directly to the ledger outside of flight (to avoid
        /// double-adding when the recording is committed).
        /// </summary>
        private static bool IsFlightScene()
        {
            return HighLogic.LoadedScene == GameScenes.FLIGHT;
        }

        /// <summary>
        /// Pure: returns true if the status transition is a real change (not an identity
        /// transition like Dead->Dead). Extracted for testability (Bug #122).
        /// </summary>
        internal static bool IsRealStatusChange(ProtoCrewMember.RosterStatus oldStatus,
            ProtoCrewMember.RosterStatus newStatus)
        {
            return oldStatus != newStatus;
        }

        /// <summary>
        /// Returns true if a crew status change event should be suppressed.
        /// Suppresses when: (1) bulk mutation in progress, (2) crew is managed by Parsek
        /// (reserved/stand-in — KSP oscillates their status as noise), (3) identity
        /// transition (same status, e.g. Dead→Dead).
        /// Pure static for testability — no KSP state access.
        /// </summary>
        internal static bool ShouldSuppressCrewStatusChange(
            string crewName, bool suppressFlag, bool isIdentity)
        {
            if (suppressFlag) return true;
            if (crewName != null && (LedgerOrchestrator.Kerbals?.IsManaged(crewName) ?? false)) return true;
            if (isIdentity) return true;
            return false;
        }

        private void OnKerbalStatusChange(ProtoCrewMember crew,
            ProtoCrewMember.RosterStatus oldStatus, ProtoCrewMember.RosterStatus newStatus)
        {
            if (crew == null) return;

            bool isIdentity = !IsRealStatusChange(oldStatus, newStatus);
            if (ShouldSuppressCrewStatusChange(crew.name, SuppressCrewEvents, isIdentity))
            {
                if (SuppressCrewEvents)
                    ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-crew-status",
                        "Suppressed CrewStatusChanged event while Parsek mutates crew state", 5.0);
                else if (isIdentity)
                    ParsekLog.Verbose("GameStateRecorder",
                        $"Filtered identity crew status transition for '{crew.name}': {oldStatus} -> {newStatus}");
                else
                    ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-managed-crew",
                        $"Suppressed CrewStatusChanged for managed kerbal '{crew.name}': {oldStatus} -> {newStatus}", 5.0);
                return;
            }

            var name = crew.name ?? "";

            double ut = Planetarium.GetUniversalTime();

            // Debounce: if we have a pending event for this crew within 0.1s game time
            // and this event reverses the previous one, cancel both.
            PendingCrewEvent prev;
            if (pendingCrewEvents.TryGetValue(name, out prev) && Math.Abs(prev.gameEvent.ut - ut) < 0.1)
            {
                if (prev.to == oldStatus && newStatus == prev.from)
                {
                    GameStateStore.RemoveEvent(prev.gameEvent);
                    pendingCrewEvents.Remove(name);
                    ParsekLog.Verbose("GameStateRecorder", $"Debounced crew status bounce for '{name}'");
                    return;
                }
            }

            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.CrewStatusChanged,
                key = name,
                detail = $"from={oldStatus};to={newStatus}"
            };
            GameStateStore.AddEvent(evt);
            pendingCrewEvents[name] = new PendingCrewEvent { gameEvent = evt, from = oldStatus, to = newStatus };
            ParsekLog.Info("GameStateRecorder", $"Game state: CrewStatusChanged '{name}' {oldStatus} → {newStatus}");
        }

        private void OnKerbalTypeChange(ProtoCrewMember pcm, ProtoCrewMember.KerbalType fromType, ProtoCrewMember.KerbalType toType)
        {
            if (SuppressCrewEvents) return;

            // Only care about Unowned -> Crew transitions (rescue pickup)
            if (fromType != ProtoCrewMember.KerbalType.Unowned || toType != ProtoCrewMember.KerbalType.Crew)
                return;

            string name = pcm?.name ?? "";
            string trait = pcm?.experienceTrait?.TypeName ?? "Pilot";

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.KerbalRescued,
                key = name,
                detail = $"trait={trait}"
            });

            ParsekLog.Info("GameStateRecorder", $"Game state: KerbalRescued '{name}' (trait={trait})");
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

            if (SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-funds",
                    $"Suppressed FundsChanged event ({reason}) during timeline replay", 5.0);
                return;
            }
            if (double.IsNaN(oldFunds)) return;
            double delta = newFunds - oldFunds;
            if (Math.Abs(delta) < FundsThreshold)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "funds-threshold",
                    $"Ignored FundsChanged delta={delta:+0.0;-0.0} below threshold={FundsThreshold:F1}", 5.0);
                return;
            }

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.FundsChanged,
                key = reason.ToString(),
                valueBefore = oldFunds,
                valueAfter = newFunds
            });
            ParsekLog.Info("GameStateRecorder", $"Game state: FundsChanged {delta:+0;-0} ({reason}) → {newFunds:F0}");
        }

        private void OnScienceChanged(float newScience, TransactionReasons reason)
        {
            double oldScience = lastScience;
            lastScience = newScience;

            if (SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-science",
                    $"Suppressed ScienceChanged event ({reason}) during timeline replay", 5.0);
                return;
            }
            if (double.IsNaN(oldScience)) return;
            double delta = newScience - oldScience;
            if (Math.Abs(delta) < ScienceThreshold)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "science-threshold",
                    $"Ignored ScienceChanged delta={delta:+0.0;-0.0} below threshold={ScienceThreshold:F1}", 5.0);
                return;
            }

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ScienceChanged,
                key = reason.ToString(),
                valueBefore = oldScience,
                valueAfter = newScience
            });
            ParsekLog.Info("GameStateRecorder", $"Game state: ScienceChanged {delta:+0.0;-0.0} ({reason}) → {newScience:F1}");
        }

        private void OnReputationChanged(float newReputation, TransactionReasons reason)
        {
            float oldReputation = lastReputation;
            lastReputation = newReputation;

            if (SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-reputation",
                    $"Suppressed ReputationChanged event ({reason}) during timeline replay", 5.0);
                return;
            }
            if (float.IsNaN(oldReputation)) return;
            float delta = newReputation - oldReputation;
            if (Math.Abs(delta) < ReputationThreshold)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "reputation-threshold",
                    $"Ignored ReputationChanged delta={delta:+0.0;-0.0} below threshold={ReputationThreshold:F1}", 5.0);
                return;
            }

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ReputationChanged,
                key = reason.ToString(),
                valueBefore = oldReputation,
                valueAfter = newReputation
            });
            ParsekLog.Info("GameStateRecorder", $"Game state: ReputationChanged {delta:+0.0;-0.0} ({reason}) → {newReputation:F1}");
        }

        #endregion

        #region Science Subject Tracking

        private void OnScienceReceived(float amount, ScienceSubject subject, ProtoVessel vessel, bool reverseEngineered)
        {
            // SuppressResourceEvents is set during timeline replay (ApplyResourceDeltas,
            // ApplyTreeLumpSum) where AddScience replays pool deltas. We must not
            // re-capture subjects during replay — they are already committed.
            if (SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-science-subject",
                    "Suppressed OnScienceReceived during timeline replay", 5.0);
                return;
            }

            if (subject == null || string.IsNullOrEmpty(subject.id))
            {
                ParsekLog.Verbose("GameStateRecorder", "OnScienceReceived: skipped — null subject or empty id");
                return;
            }
            if (amount <= 0)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"OnScienceReceived: skipped — non-positive amount ({amount:F1}) for {subject.id}");
                return;
            }

            // Record the cumulative science earned for this subject.
            // Note: subject.science may include Harmony-injected committed science
            // (from ScienceSubjectPatch) if this experiment was previously committed.
            // This is correct — max-wins in CommitScienceSubjects ensures the value
            // only increases, reflecting the true highest science earned.
            PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subject.id,
                science = subject.science,
                subjectMaxValue = subject.scienceCap
            });

            ParsekLog.Info("GameStateRecorder",
                $"Science subject captured: {subject.id} amount={amount:F1} total={subject.science:F1}");
        }

        #endregion

        #region Progress Milestone Handlers

        private void OnProgressComplete(ProgressNode node)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed MilestoneAchieved event during action replay");
                return;
            }
            if (node == null)
            {
                ParsekLog.Verbose("GameStateRecorder", "OnProgressComplete: null node — skipped");
                return;
            }

            string milestoneId = QualifyMilestoneId(node);
            if (string.IsNullOrEmpty(milestoneId))
            {
                ParsekLog.Verbose("GameStateRecorder", "OnProgressComplete: empty node Id — skipped");
                return;
            }

            // Funds and rep rewards are not directly available on the ProgressNode.
            // They are applied separately by KSP's ProgressTracking system via
            // Funding/Reputation callbacks. We capture 0 here; the MilestonesModule
            // sets Effective flags correctly regardless.
            //
            // OnProgressComplete fires AFTER KSP has already applied the reward via
            // Funding.Instance.AddFunds / Reputation.Instance.AddReputation. In theory
            // we could compute the delta by comparing pre/post values, but we don't have
            // a pre-event snapshot (no prefix hook). The reward amounts come from
            // GameVariables.Instance.GetProgressFunds/Rep/Science() which vary by
            // body, milestone type, and difficulty settings — too fragile to replicate.
            // See deferred items D17/D18 for earning-side capture plans.
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.MilestoneAchieved,
                key = milestoneId,
                detail = ""
            };
            GameStateStore.AddEvent(evt);
            ParsekLog.Info("GameStateRecorder", $"Game state: MilestoneAchieved '{milestoneId}'");

            // Milestones can fire at KSC (e.g., facility-related) or in flight.
            // Write directly to ledger when outside flight to avoid waiting for commit.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Pure: creates a MilestoneAchieved GameStateEvent from the given parameters.
        /// Extracted for testability.
        /// </summary>
        internal static GameStateEvent CreateMilestoneEvent(string milestoneId, double ut)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = milestoneId ?? "",
                detail = ""
            };
        }

        /// <summary>
        /// Path-qualifies body-specific milestone IDs to avoid ambiguity.
        /// Body-specific achievement classes (CelestialBodyLanding, CelestialBodyOrbit, etc.)
        /// all have a private CelestialBody body field. The bare node.Id (e.g. "Landing")
        /// is non-unique across bodies, so we qualify as "Mun/Landing", "Kerbin/Landing", etc.
        ///
        /// Top-level nodes (FirstLaunch, ReachSpace, RecordsAltitude, etc.) have unique IDs
        /// and are returned as-is.
        ///
        /// Internal static for testability.
        /// </summary>
        internal static string QualifyMilestoneId(ProgressNode node)
        {
            if (node == null) return "";

            string bareId = node.Id ?? "";
            if (string.IsNullOrEmpty(bareId)) return "";

            // Try reflection to get the private "body" field present on all
            // CelestialBody* achievement subclasses (Landing, Orbit, Flyby, etc.)
            var bodyField = node.GetType().GetField("body",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (bodyField != null)
            {
                var celestialBody = bodyField.GetValue(node) as CelestialBody;
                if (celestialBody != null)
                {
                    string qualified = celestialBody.name + "/" + bareId;
                    ParsekLog.Verbose("GameStateRecorder",
                        $"QualifyMilestoneId: qualified '{bareId}' -> '{qualified}' (body={celestialBody.name})");
                    return qualified;
                }
            }

            // No body field — top-level node with unique ID (FirstLaunch, ReachSpace, etc.)
            return bareId;
        }

        #endregion

        #region Facility Polling

        /// <summary>
        /// Seeds the facility/building cache from current game state.
        /// Always uses live KSP state rather than event history to avoid
        /// stale data from abandoned future branches after reverts.
        /// Call before Subscribe()/PollFacilityState().
        /// </summary>
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
        /// Polls current facility/building state and emits events for any changes
        /// since the cached state. Called on Subscribe() after cache is seeded.
        /// </summary>
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
                            GameStateStore.AddEvent(evt);
                            eventsEmitted++;
                            ParsekLog.Info("GameStateRecorder", $"Game state: {eventType} '{kvp.Key}' {cachedLevel:F2} → {currentLevel:F2}");

                            if (!IsFlightScene() && eventType == GameStateEventType.FacilityUpgraded)
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

                            GameStateStore.AddEvent(new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = db.id
                            });
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
