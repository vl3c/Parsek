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

        /// <summary>
        /// #431: test hook. When non-null, <see cref="ResolveCurrentRecordingTag"/> returns its result
        /// instead of probing <see cref="ParsekFlight"/> / <see cref="RecordingStore"/>. Unit tests
        /// set this to simulate a live recording without spinning up the MonoBehaviour.
        /// Production code never touches it.
        /// </summary>
        internal static System.Func<string> TagResolverForTesting;

        /// <summary>
        /// #431: central funnel for every <see cref="GameStateEvent"/> the recorder produces.
        /// Resolves the current recording tag, stamps it on the event, logs the emission, and warns
        /// on drift (tagged event with no live recorder / no pending vessel-switch, or in-flight event
        /// with an active recorder but no tag). All captured-event sites in this class route through
        /// <c>Emit</c>.
        /// </summary>
        internal static void Emit(GameStateEvent evt, string source)
        {
            string tag = ResolveCurrentRecordingTag();
            if (string.IsNullOrEmpty(evt.recordingId))
                evt.recordingId = tag ?? "";

            ParsekLog.Verbose("GameStateRecorder",
                $"Emit: {evt.eventType} key='{evt.key}' tag='{evt.recordingId}' source='{source ?? ""}'");

            bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
            bool midSwitch = RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch;
            bool flightAlive = ParsekFlight.Instance != null;
            // Drift A: non-empty tag with no live flight context. During FLIGHT -> KSC transitions,
            // ParsekFlight.Instance lingers for a few frames while the scene enum already reads
            // SPACECENTER and recovery-reward events fire — that path is legitimate, so the warn
            // gates on Instance == null (not just scene != FLIGHT).
            if (!inFlight && !midSwitch && !flightAlive && !string.IsNullOrEmpty(tag))
                ParsekLog.Warn("GameStateRecorder",
                    $"Emit drift: event '{evt.eventType}' tagged '{tag}' with no live flight context — stale tag?");
            // Drift B: in flight with a live recorder but no tag resolved.
            if (inFlight && string.IsNullOrEmpty(tag) && HasLiveRecorder())
                ParsekLog.Warn("GameStateRecorder",
                    $"Emit drift: event '{evt.eventType}' in-flight with live recorder but empty tag");

            GameStateStore.AddEvent(evt);
        }

        /// <summary>
        /// #431: resolves the current recording id for event tagging.
        /// Primary source is <see cref="ParsekFlight.GetActiveRecordingIdForTagging"/>; the fallback
        /// reads <see cref="RecordingStore.PendingTree"/>.ActiveRecordingId while the pending tree is in
        /// <see cref="PendingTreeState.LimboVesselSwitch"/> — events captured during a vessel-switch stash
        /// belong to the outgoing recording.
        /// </summary>
        internal static string ResolveCurrentRecordingTag()
        {
            if (TagResolverForTesting != null)
                return TagResolverForTesting() ?? "";

            var live = ParsekFlight.GetActiveRecordingIdForTagging();
            if (!string.IsNullOrEmpty(live)) return live;

            if (RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch)
            {
                var pend = RecordingStore.PendingTree?.ActiveRecordingId;
                if (!string.IsNullOrEmpty(pend)) return pend;
            }

            return "";
        }

        /// <summary>
        /// #431: true when a flight recorder is currently live on the active tree. Used by
        /// <see cref="Emit"/>'s drift-warn branch to flag "in-flight, should have a tag, doesn't."
        /// </summary>
        internal static bool HasLiveRecorder() => ParsekFlight.HasLiveRecorderForTagging();

        internal static void ResetForTesting()
        {
            TagResolverForTesting = null;
            PendingScienceSubjects.Clear();
            SuppressCrewEvents = false;
            SuppressResourceEvents = false;
            IsReplayingActions = false;
        }

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
            // #416: OnCrewmemberHired (fired from KerbalRoster.HireApplicant) — NOT
            // onKerbalAdded, which also fires for applicant pool generation and the
            // four starter kerbals at new-career creation, triggering spurious
            // KerbalHire debits that wiped starting funds.
            GameEvents.OnCrewmemberHired.Add(OnCrewmemberHired);
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
            GameEvents.OnCrewmemberHired.Remove(OnCrewmemberHired);
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
            float advanceFunds = (float)contract.FundsAdvance;
            float failFunds = (float)contract.FundsFailure;
            float failRep = (float)contract.ReputationFailure;

            // Structured detail: title + deadline + advance + failure penalties.
            // advance must be captured at accept time — KSP applies it immediately via
            // FundsChanged(ContractAdvance), which the converter intentionally drops.
            // Without funds= here, AdvanceFunds stayed 0 and FundsModule never credited
            // the advance (codex review [P1] on PR #307).
            // deadline=0 means no deadline (KSP convention) — store as NaN.
            string deadlineStr = deadline > 0
                ? deadline.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "NaN";
            var detail = $"title={title};deadline={deadlineStr}" +
                $";funds={advanceFunds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";failFunds={failFunds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";failRep={failRep.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractAccepted,
                key = guid,
                detail = detail
            };
            Emit(evt, "ContractAccepted");

            // Store full contract snapshot for reversal
            try
            {
                var contractNode = new ConfigNode("CONTRACT");
                contract.Save(contractNode);
                GameStateStore.AddContractSnapshot(guid, contractNode);
                ParsekLog.Info("GameStateRecorder",
                    $"Game state: ContractAccepted '{title}' deadline={deadlineStr} " +
                    $"advance={advanceFunds} failFunds={failFunds} failRep={failRep} (snapshot saved)");
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
            // InvariantCulture-safe float serialization — plain interpolation emits
            // comma decimals on locales like de-DE/fr-FR/ro-RO, and the IC-parsing
            // converter side then reads zero. See also OnContractAccepted.
            float fundsReward = (float)contract.FundsCompletion;
            float repReward = (float)contract.ReputationCompletion;
            float sciReward = (float)contract.ScienceCompletion;
            var detail = $"title={title}" +
                $";fundsReward={fundsReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";repReward={repReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";sciReward={sciReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCompleted,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            Emit(evt, "ContractCompleted");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractCompleted '{title}' (funds={fundsReward}, rep={repReward}, sci={sciReward})");

            // #405: route to ledger immediately when at KSC (contract completions in flight
            // go through the normal commit-time ConvertEvents path).
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractFailed(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            float fundsPenalty = (float)contract.FundsFailure;
            float repPenalty = (float)contract.ReputationFailure;
            var detail = $"title={title}" +
                $";fundsPenalty={fundsPenalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";repPenalty={repPenalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractFailed,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            Emit(evt, "ContractFailed");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractFailed '{title}' (fundsPenalty={fundsPenalty}, repPenalty={repPenalty})");

            // #405: route to ledger immediately when at KSC.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractCancelled(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            float fundsPenalty = (float)contract.FundsFailure;
            float repPenalty = (float)contract.ReputationFailure;
            var detail = $"title={title}" +
                $";fundsPenalty={fundsPenalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
                $";repPenalty={repPenalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractCancelled,
                key = contract.ContractGuid.ToString(),
                detail = detail
            };
            Emit(evt, "ContractCancelled");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractCancelled '{title}' (fundsPenalty={fundsPenalty}, repPenalty={repPenalty})");

            // #405: route to ledger immediately when at KSC.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractDeclined(Contract contract)
        {
            if (contract == null) return;
            var title = contract.Title ?? "";
            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractDeclined,
                key = contract.ContractGuid.ToString(),
                detail = title
            }, "ContractDeclined");
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
            Emit(evt, "TechResearched");
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
            // InvariantCulture-safe: plain interpolation serializes floats with the
            // system locale decimal separator, and ConvertPartPurchased parses with IC.
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            float cost = part.cost;

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.PartPurchased,
                key = partName,
                detail = "cost=" + cost.ToString("R", ic),
                valueBefore = Funding.Instance != null ? Funding.Instance.Funds + cost : 0,
                valueAfter = Funding.Instance != null ? Funding.Instance.Funds : 0
            };
            Emit(evt, "PartPurchased");
            ParsekLog.Info("GameStateRecorder", $"Game state: PartPurchased '{partName}' (cost={cost})");

            // #405: route to ledger immediately when at KSC. Relies on the DedupKey (§F)
            // to disambiguate part-name collisions.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        #endregion

        #region Crew Handlers

        /// <summary>
        /// Fires from KerbalRoster.HireApplicant when the player pays to recruit an
        /// applicant from the Astronaut Complex. <paramref name="activeCrewCount"/> is
        /// KSP's pre-hire crew count — the same value GetRecruitHireCost takes to pick
        /// the cost curve tier.
        /// </summary>
        private void OnCrewmemberHired(ProtoCrewMember crew, int activeCrewCount)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed CrewHired event during action replay");
                return;
            }
            if (SuppressCrewEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-crew-hired",
                    "Suppressed CrewHired event while Parsek mutates crew state", 5.0);
                return;
            }
            if (crew == null) return;
            var name = crew.name ?? "";

            // Hire cost from KSP's career curve. activeCrewCount is passed in by
            // KerbalRoster; prefer it over re-reading the roster to avoid a race with
            // the upcoming type flip (Applicant -> Crew) inside HireApplicant.
            float hireCost = ComputeHireCost(activeCrewCount);

            // InvariantCulture-safe hire cost — GetRecruitHireCost is typically integer
            // but could return fractional on modded career curves.
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewHired,
                key = name,
                detail = $"trait={crew.trait ?? ""};cost={hireCost.ToString("R", ic)}"
            };
            Emit(evt, "CrewHired");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: CrewHired '{name}' ({crew.trait ?? "?"}) " +
                $"cost={hireCost.ToString("R", ic)} activeCrewCount={activeCrewCount}");

            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Returns the KSP hire cost for the Nth recruit. Safe to call in tests or when
        /// GameVariables is not yet initialized — returns 0f in that case.
        /// </summary>
        internal static float ComputeHireCost(int activeCrewCount)
        {
            if (GameVariables.Instance == null || HighLogic.CurrentGame == null)
                return 0f;
            return (float)GameVariables.Instance.GetRecruitHireCost(activeCrewCount);
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

            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewRemoved,
                key = name,
                detail = $"trait={crew.trait ?? ""}"
            }, "KerbalRemoved");
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
            Emit(evt, "KerbalStatusChange");
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

            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.KerbalRescued,
                key = name,
                detail = $"trait={trait}"
            }, "KerbalRescued");

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

            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.FundsChanged,
                key = reason.ToString(),
                valueBefore = oldFunds,
                valueAfter = newFunds
            }, "FundsChanged");
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

            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ScienceChanged,
                key = reason.ToString(),
                valueBefore = oldScience,
                valueAfter = newScience
            }, "ScienceChanged");
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

            Emit(new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ReputationChanged,
                key = reason.ToString(),
                valueBefore = oldReputation,
                valueAfter = newReputation
            }, "ReputationChanged");
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

            // #400: KSP's reward pipeline is:
            //   subclass handler -> ProgressNode.Complete() -> OnProgressComplete.Fire()
            //                    -> [this handler runs here, rewards NOT yet applied]
            //                    -> subclass calls AwardProgressStandard() -> AwardProgress(..funds, sci, rep..)
            //
            // Because rewards land AFTER this subscriber fires, we emit the event with
            // zeros as defaults and rely on the AwardProgressPatch Harmony postfix to
            // update the detail in place once it has the real reward values. For non-
            // career modes the zeros are correct (no rewards apply).
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.MilestoneAchieved,
                key = milestoneId,
                detail = BuildMilestoneDetail(0.0, 0f, 0.0)
            };
            Emit(evt, "MilestoneAchieved");

            // Tag the node -> event association so the AwardProgress postfix can find
            // and enrich the right entry. The key is the ProgressNode instance reference,
            // which is unique per milestone within the current stack frame.
            PendingMilestoneEventByNode[node] = evt;

            ParsekLog.Info("GameStateRecorder",
                $"Game state: MilestoneAchieved '{milestoneId}' (awaiting reward enrichment)");

            // Milestones can fire at KSC (e.g., facility-related) or in flight.
            // Write directly to ledger when outside flight to avoid waiting for commit.
            if (!IsFlightScene())
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Formats milestone reward values into the detail string format that
        /// <see cref="GameStateEventConverter.ConvertMilestoneAchieved"/> parses.
        /// Internal static for testability.
        /// </summary>
        internal static string BuildMilestoneDetail(double funds, float rep, double sci)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return $"funds={funds.ToString("R", ic)};" +
                   $"rep={rep.ToString("R", ic)};" +
                   $"sci={sci.ToString("R", ic)}";
        }

        /// <summary>
        /// Short-lived map of ProgressNode -> pending MilestoneAchieved event. Populated
        /// by <see cref="OnProgressComplete"/> when the event is emitted with zero
        /// rewards, and read by <see cref="EnrichPendingMilestoneRewards"/> (called from
        /// the Harmony postfix on <c>ProgressNode.AwardProgress</c>) to update the detail
        /// string in place with the real funds/rep/sci values from the award parameters.
        ///
        /// Entries are removed immediately after enrichment. Stale entries (if AwardProgress
        /// never fires for a node) don't survive beyond the save session — on load the map
        /// is empty.
        /// </summary>
        internal static readonly Dictionary<ProgressNode, GameStateEvent> PendingMilestoneEventByNode
            = new Dictionary<ProgressNode, GameStateEvent>();

        /// <summary>
        /// Called from the Harmony postfix on <c>ProgressNode.AwardProgress</c>. Looks
        /// up the pending MilestoneAchieved event for the given node and updates its
        /// detail string with the real funds/rep/sci values. If no pending event is
        /// found (e.g., non-career mode where the subscriber didn't run), this is a
        /// no-op. Also updates any matching ledger action that was already written via
        /// <see cref="LedgerOrchestrator.OnKscSpending"/> — otherwise KSC milestones would
        /// have zero rewards in the ledger.
        /// Internal static for testability.
        /// </summary>
        internal static void EnrichPendingMilestoneRewards(
            ProgressNode node, double funds, float rep, double sci)
        {
            if (node == null) return;
            if (!PendingMilestoneEventByNode.TryGetValue(node, out var evt))
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"EnrichPendingMilestoneRewards: no pending event for node '{node.Id ?? "<null>"}' — skip");
                return;
            }

            // Build the new detail string and patch the store's copy in place.
            // GameStateEvent is a value type; the cached `evt` is a separate copy that
            // we must also update for the test hooks and downstream observers.
            string newDetail = BuildMilestoneDetail(funds, rep, sci);
            GameStateStore.UpdateEventDetail(evt.ut, evt.eventType, evt.key, evt.epoch, newDetail);
            evt.detail = newDetail;
            PendingMilestoneEventByNode.Remove(node);

            // If the event was already forwarded to the ledger via OnKscSpending, the
            // ledger has a zero-reward MilestoneAchievement action we need to update.
            // Find it by matching type + UT + milestoneId.
            if (LedgerOrchestrator.IsInitialized)
            {
                var ledgerActions = Ledger.Actions;
                for (int i = ledgerActions.Count - 1; i >= 0; i--)
                {
                    var a = ledgerActions[i];
                    if (a.Type != GameActionType.MilestoneAchievement) continue;
                    if (System.Math.Abs(a.UT - evt.ut) > 0.1) continue;
                    if (a.MilestoneId != evt.key) continue;

                    a.MilestoneFundsAwarded = (float)funds;
                    a.MilestoneRepAwarded = rep;
                    a.MilestoneScienceAwarded = (float)sci;
                    ParsekLog.Verbose("GameStateRecorder",
                        $"EnrichPendingMilestoneRewards: updated ledger action for '{evt.key}' " +
                        $"funds={funds:F0} rep={rep:F1} sci={sci:F1}");
                    break;
                }
            }

            ParsekLog.Info("GameStateRecorder",
                $"Milestone enriched: '{evt.key}' funds={funds:F0} rep={rep:F1} sci={sci:F1}");
        }

        /// <summary>
        /// Reflection helper: extracts the private "body" CelestialBody field from
        /// body-specific ProgressNode subclasses (CelestialBodyLanding, CelestialBodyOrbit,
        /// CelestialBodyFlyby, etc.). Returns null for top-level nodes that have no body.
        /// Internal static for testability.
        /// </summary>
        internal static CelestialBody ExtractNodeBody(ProgressNode node)
        {
            if (node == null) return null;
            var bodyField = node.GetType().GetField("body",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (bodyField == null) return null;
            return bodyField.GetValue(node) as CelestialBody;
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
                            Emit(evt, eventType.ToString());
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

                            Emit(new GameStateEvent
                            {
                                ut = ut,
                                eventType = eventType,
                                key = db.id
                            }, eventType.ToString());
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
