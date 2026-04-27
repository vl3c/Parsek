using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// Converted into ScienceEarning actions and then mirrored into
        /// GameStateStore.committedScienceSubjects on commit.
        /// </summary>
        internal static List<PendingScienceSubject> PendingScienceSubjects = new List<PendingScienceSubject>();

        internal struct RecentScienceChangeCapture
        {
            public double Ut;
            public string ReasonKey;
            public float Delta;
            public string RecordingId;
            public bool Valid;
        }

        /// <summary>
        /// #431: test hook. When non-null, <see cref="ResolveCurrentRecordingTag"/> returns its result
        /// instead of probing <see cref="ParsekFlight"/> / <see cref="RecordingStore"/>. Unit tests
        /// set this to simulate a live recording without spinning up the MonoBehaviour.
        /// Production code never touches it.
        /// </summary>
        internal static System.Func<string> TagResolverForTesting;
        internal static System.Func<bool> HasLiveRecorderProviderForTesting;
        internal static System.Func<bool> HasActiveUncommittedTreeProviderForTesting;

        /// <summary>
        /// #431: central funnel for every <see cref="GameStateEvent"/> the recorder produces.
        /// Resolves the current recording tag, stamps it on the event, logs the emission, and warns
        /// on drift (tagged event with no live recorder / no pending vessel-switch, or in-flight event
        /// with an active recorder but no tag). All captured-event sites in this class route through
        /// <c>Emit</c>.
        /// </summary>
        internal static void Emit(ref GameStateEvent evt, string source)
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

            // `ref evt` propagates any AddEvent-side normalization back to the caller's
            // local copy. Callers that cache `evt` after Emit (e.g.
            // RegisterPendingMilestoneEvent) see the final recordingId / legacy-field
            // shape automatically, so there is no value-type field-mirror footgun to
            // work around.
            GameStateStore.AddEvent(ref evt);
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
        internal static bool HasLiveRecorder()
        {
            var provider = HasLiveRecorderProviderForTesting;
            if (provider != null)
                return provider();

            return ParsekFlight.HasLiveRecorderForTagging();
        }

        internal static bool HasActiveUncommittedTree()
        {
            var provider = HasActiveUncommittedTreeProviderForTesting;
            if (provider != null)
                return provider();

            return ParsekFlight.HasUncommittedTreeForKspPatchDeferral();
        }

        internal static string FormatEventRejectSummary(
            string source,
            string reason,
            string key = null,
            string detail = null)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Event rejected: source={0} reason={1} key={2} detail={3}",
                string.IsNullOrEmpty(source) ? "(unknown)" : source,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.IsNullOrEmpty(key) ? "(none)" : key,
                string.IsNullOrEmpty(detail) ? "(none)" : detail);
        }

        private static void LogEventReject(
            string source,
            string reason,
            string key = null,
            string detail = null)
        {
            string rateKey = string.Format(CultureInfo.InvariantCulture,
                "reject-{0}-{1}-{2}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.IsNullOrEmpty(key) ? "none" : key);
            ParsekLog.VerboseRateLimited("GameStateRecorder",
                rateKey,
                FormatEventRejectSummary(source, reason, key, detail),
                5.0);
        }

        internal static void ResetForTesting()
        {
            TagResolverForTesting = null;
            HasLiveRecorderProviderForTesting = null;
            HasActiveUncommittedTreeProviderForTesting = null;
            ClearPendingMilestoneEvents("ResetForTesting");
            PendingScienceSubjects.Clear();
            SuppressCrewEvents = false;
            SuppressResourceEvents = false;
            IsReplayingActions = false;
            BypassEntryPurchaseAfterResearchProviderForTesting = null;
        }

        /// <summary>
        /// Test-only seam for the stock R&amp;D-part-purchase difficulty toggle. Production
        /// reads <c>HighLogic.CurrentGame.Parameters.Difficulty.BypassEntryPurchaseAfterResearch</c>
        /// directly; tests install a provider to drive both branches without a live KSP game.
        /// Cleared by <see cref="ResetForTesting"/>.
        /// </summary>
        internal static Func<bool> BypassEntryPurchaseAfterResearchProviderForTesting;

        /// <summary>
        /// Returns whether the stock R&amp;D difficulty toggle is available and, if so,
        /// whether KSP bypasses one-time part entry purchases after research.
        /// </summary>
        internal static bool TryGetBypassEntryPurchaseAfterResearch(out bool bypassEntryPurchaseAfterResearch)
        {
            var provider = BypassEntryPurchaseAfterResearchProviderForTesting;
            if (provider != null)
            {
                bypassEntryPurchaseAfterResearch = provider();
                return true;
            }

            var game = HighLogic.CurrentGame;
            if (game == null || game.Parameters == null || game.Parameters.Difficulty == null)
            {
                bypassEntryPurchaseAfterResearch = false;
                return false;
            }

            bypassEntryPurchaseAfterResearch =
                game.Parameters.Difficulty.BypassEntryPurchaseAfterResearch;
            return true;
        }

        /// <summary>
        /// Returns whether stock KSP bypasses the one-time part entry purchase in R&amp;D.
        /// Defensive: returns false when no live game exists.
        /// </summary>
        internal static bool IsBypassEntryPurchaseAfterResearch()
        {
            bool bypassEntryPurchaseAfterResearch;
            return TryGetBypassEntryPurchaseAfterResearch(out bypassEntryPurchaseAfterResearch)
                && bypassEntryPurchaseAfterResearch;
        }

        /// <summary>
        /// Builds the canonical PartPurchased event payload from stock KSP semantics:
        /// when bypass is on, the player pays 0; when bypass is off, the player pays
        /// the part's <c>entryCost</c> (not its rollout/build <c>cost</c>).
        /// Internal static for unit test coverage.
        /// </summary>
        internal static GameStateEvent CreatePartPurchasedEvent(
            string partName,
            float entryCost,
            bool bypassEntryPurchaseAfterResearch,
            double ut,
            double currentFunds)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            float chargedCost = bypassEntryPurchaseAfterResearch ? 0f : entryCost;
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.PartPurchased,
                key = partName ?? "",
                detail = "cost=" + chargedCost.ToString("R", ic),
                valueBefore = currentFunds + chargedCost,
                valueAfter = currentFunds
            };
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
        private RecentScienceChangeCapture latestScienceChangeCapture;

        // Resource tracking for threshold checks
        private double lastFunds = double.NaN;
        private double lastScience = double.NaN;
        private float lastReputation = float.NaN;

        private const double FundsThreshold = 100.0;
        private const double ScienceThreshold = 0.001;
        private const double ScienceCaptureMatchWindowSeconds = 5.0;
        private const float ScienceCaptureMatchDeltaTolerance = 0.05f;
        private const float ReputationThreshold = 1.0f;
        private const float ReputationThresholdEpsilon = 0.001f;

        #region Subscription Management

        internal void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            pendingCrewEvents.Clear();
            latestScienceChangeCapture = default(RecentScienceChangeCapture);
            ClearPendingMilestoneEvents("Subscribe");

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
            latestScienceChangeCapture = default(RecentScienceChangeCapture);
            ClearPendingMilestoneEvents("Unsubscribe");

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
            if (contract == null)
            {
                LogEventReject("ContractOffered", "contract-null");
                return;
            }
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
            if (contract == null)
            {
                LogEventReject("ContractAccepted", "contract-null");
                return;
            }
            string guid = contract.ContractGuid.ToString();

            var title = contract.Title ?? "";
            double deadline = contract.TimeDeadline;
            float advanceFunds = (float)contract.FundsAdvance;
            float failFunds = (float)contract.FundsFailure;
            float failRep = (float)contract.ReputationFailure;
            string contractType = contract.GetType().Name ?? "";
            ConfigNode contractNode = null;
            Exception snapshotFailure = null;

            try
            {
                contractNode = new ConfigNode("CONTRACT");
                contract.Save(contractNode);
                string savedContractType = contractNode.GetValue("type");
                if (!string.IsNullOrEmpty(savedContractType))
                    contractType = savedContractType;
            }
            catch (Exception ex)
            {
                snapshotFailure = ex;
                contractNode = null;
            }

            // Structured detail: title + deadline + type + advance + failure penalties.
            // advance must be captured at accept time — KSP applies it immediately via
            // FundsChanged(ContractAdvance), which the converter intentionally drops.
            // Without funds= here, AdvanceFunds stayed 0 and FundsModule never credited
            // the advance (codex review [P1] on PR #307). type= must also be captured
            // here because ContractAccept ledger actions are consumed by UI/policy paths
            // that do not re-open the stored snapshot.
            // deadline=0 means no deadline (KSP convention) — store as NaN.
            string deadlineStr = deadline > 0
                ? deadline.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "NaN";
            var detail = $"title={title};deadline={deadlineStr}" +
                (string.IsNullOrEmpty(contractType) ? "" : $";type={contractType}") +
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
            Emit(ref evt, "ContractAccepted");

            // Store full contract snapshot for reversal
            if (contractNode != null)
            {
                GameStateStore.AddContractSnapshot(guid, contractNode);
                ParsekLog.Info("GameStateRecorder",
                    $"Game state: ContractAccepted '{title}' type='{contractType}' deadline={deadlineStr} " +
                    $"advance={advanceFunds} failFunds={failFunds} failRep={failRep} (snapshot saved)");
            }
            else
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"Game state: ContractAccepted '{title}' type='{contractType}' " +
                    $"(snapshot FAILED: {(snapshotFailure != null ? snapshotFailure.Message : "unknown error")})");
            }

            // Write directly to ledger when this event has no recording owner and no
            // live recorder can still claim it. Tagged contract events flow through
            // the normal commit-time ConvertEvents path. Untagged pre-recording
            // FLIGHT events must be direct-ledger too: stock can complete launch-site
            // contracts just before Parsek auto-record starts, before a live recorder
            // exists.
            // #405: without this, accepted contracts at KSC never reached the ledger and
            // PatchContracts had no active contracts to preserve on next recalc.
            // #431 gate: during FLIGHT -> SPACECENTER teardown the scene already reads
            // SPACECENTER but ParsekFlight.Instance is still alive and Emit legitimately
            // tagged this event with the outgoing recordingId. Skip the ledger write
            // when non-empty: the tagged event is already in
            // GameStateStore and will flow through the commit-time path (or be purged
            // with the recording on discard). Without this gate the ledger would end up
            // with an untagged KSC action that survives DiscardPendingTree forever.
            // If the tag is empty while a live recorder exists, treat it as tag drift
            // and avoid creating a null-owner ledger action that survives discard.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractCompleted(Contract contract)
        {
            if (contract == null)
            {
                LogEventReject("ContractCompleted", "contract-null");
                return;
            }
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
            Emit(ref evt, "ContractCompleted");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractCompleted '{title}' (funds={fundsReward}, rep={repReward}, sci={sciReward})");

            // #405: route to ledger immediately when there is no recording owner.
            // #431 gate: see OnContractAccepted — skip the ledger write when the event
            // was tagged during FLIGHT -> SPACECENTER teardown.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractFailed(Contract contract)
        {
            if (contract == null)
            {
                LogEventReject("ContractFailed", "contract-null");
                return;
            }
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
            Emit(ref evt, "ContractFailed");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractFailed '{title}' (fundsPenalty={fundsPenalty}, repPenalty={repPenalty})");

            // #405: route to ledger immediately when there is no recording owner.
            // #431 gate: see OnContractAccepted.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractCancelled(Contract contract)
        {
            if (contract == null)
            {
                LogEventReject("ContractCancelled", "contract-null");
                return;
            }
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
            Emit(ref evt, "ContractCancelled");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: ContractCancelled '{title}' (fundsPenalty={fundsPenalty}, repPenalty={repPenalty})");

            // #405: route to ledger immediately when there is no recording owner.
            // #431 gate: see OnContractAccepted.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnContractDeclined(Contract contract)
        {
            if (contract == null)
            {
                LogEventReject("ContractDeclined", "contract-null");
                return;
            }
            var title = contract.Title ?? "";
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ContractDeclined,
                key = contract.ContractGuid.ToString(),
                detail = title
            };
            Emit(ref evt, "ContractDeclined");
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
            if (data.host == null)
            {
                LogEventReject("TechResearched", "tech-null");
                return;
            }
            if (data.target != RDTech.OperationResult.Successful)
            {
                LogEventReject(
                    "TechResearched",
                    "operation-not-successful",
                    data.host.techID,
                    data.target.ToString());
                return;
            }

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
            Emit(ref evt, "TechResearched");
            ParsekLog.Info("GameStateRecorder", $"Game state: TechResearched '{techId}' (cost={data.host.scienceCost})");

            // Write directly to ledger when there is no recording owner.
            // #553 follow-up: gate on the same ShouldForwardDirectLedgerEvent predicate
            // used by the contract handlers. Untagged pre-recording FLIGHT tech-research
            // events have no later commit-time owner; suppressing them here would
            // strand the TechResearched action in GameStateStore.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        private void OnPartPurchased(AvailablePart part)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder", "Suppressed PartPurchased event during action replay");
                return;
            }
            if (part == null)
            {
                LogEventReject("PartPurchased", "part-null");
                return;
            }
            var partName = part.name ?? "";
            // InvariantCulture-safe: plain interpolation serializes floats with the
            // system locale decimal separator, and ConvertPartPurchased parses with IC.
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            float entryCost = part.entryCost;
            float chargedCost = ComputePartPurchaseFundsSpent(entryCost);

            // #451: `cost=` stays authoritative for save/load and UI because it must
            // reflect the ACTUAL funds debit: 0 when BypassEntryPurchaseAfterResearch is
            // on, entryCost when the harder no-bypass difficulty is active. Persist the
            // raw stock entry price separately for diagnostics and future tooling.
            var detail = "cost=" + chargedCost.ToString("R", ic) +
                         ";entryCost=" + entryCost.ToString("R", ic);

            // Keep before/after in sync with the charged amount so future consumers that
            // derive deltas from the numeric fields do not resurrect the bypass=true bug.
            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.PartPurchased,
                key = partName,
                detail = detail,
                valueBefore = Funding.Instance != null ? Funding.Instance.Funds + chargedCost : 0,
                valueAfter = Funding.Instance != null ? Funding.Instance.Funds : 0
            };
            Emit(ref evt, "PartPurchased");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: PartPurchased '{partName}' (chargedCost={chargedCost}, entryCost={entryCost})");

            // #405: route to ledger immediately when there is no recording owner.
            // Relies on the DedupKey (§F) to disambiguate part-name collisions.
            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so untagged
            // pre-recording FLIGHT part-purchases reach the ledger too.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Returns the actual funds debit for an R&D part purchase. Stock KSP still
        /// fires OnPartPurchased when BypassEntryPurchaseAfterResearch is enabled, but
        /// the purchase is free in that mode.
        /// </summary>
        internal static float ComputePartPurchaseFundsSpent(float entryCost)
        {
            return IsBypassEntryPurchaseAfterResearch() ? 0f : entryCost;
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
            if (crew == null)
            {
                LogEventReject("CrewHired", "crew-null");
                return;
            }
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
            Emit(ref evt, "CrewHired");
            ParsekLog.Info("GameStateRecorder",
                $"Game state: CrewHired '{name}' ({crew.trait ?? "?"}) " +
                $"cost={hireCost.ToString("R", ic)} activeCrewCount={activeCrewCount}");

            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so untagged
            // pre-recording FLIGHT crew hires reach the ledger too.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
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
            if (crew == null)
            {
                LogEventReject("CrewRemoved", "crew-null");
                return;
            }
            var name = crew.name ?? "";

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.CrewRemoved,
                key = name,
                detail = $"trait={crew.trait ?? ""}"
            };
            Emit(ref evt, "KerbalRemoved");
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
        /// Returns true when a captured lifecycle event has no recording owner and
        /// should be written straight to the ledger. A live recorder with an empty
        /// tag is tag drift, not proof that the event is ownerless; true
        /// pre-recording FLIGHT events have no tag and no live recorder yet.
        /// </summary>
        internal static bool ShouldForwardDirectLedgerEvent(string recordingTag, bool hasLiveRecorder)
        {
            return string.IsNullOrEmpty(recordingTag) && !hasLiveRecorder;
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
            if (crew == null)
            {
                LogEventReject("CrewStatusChanged", "crew-null");
                return;
            }

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
            Emit(ref evt, "KerbalStatusChange");
            pendingCrewEvents[name] = new PendingCrewEvent { gameEvent = evt, from = oldStatus, to = newStatus };
            ParsekLog.Info("GameStateRecorder", $"Game state: CrewStatusChanged '{name}' {oldStatus} → {newStatus}");
        }

        private void OnKerbalTypeChange(ProtoCrewMember pcm, ProtoCrewMember.KerbalType fromType, ProtoCrewMember.KerbalType toType)
        {
            if (SuppressCrewEvents) return;

            if (pcm == null)
            {
                LogEventReject("KerbalTypeChanged", "crew-null");
                return;
            }

            // Only care about Unowned -> Crew transitions (rescue pickup)
            if (fromType != ProtoCrewMember.KerbalType.Unowned || toType != ProtoCrewMember.KerbalType.Crew)
                return;

            string name = pcm?.name ?? "";
            string trait = pcm?.experienceTrait?.TypeName ?? "Pilot";

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.KerbalRescued,
                key = name,
                detail = $"trait={trait}"
            };
            Emit(ref evt, "KerbalRescued");

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

            double ut = Planetarium.GetUniversalTime();
            var fundsEvt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = reason.ToString(),
                valueBefore = oldFunds,
                valueAfter = newFunds
            };
            Emit(ref fundsEvt, "FundsChanged");
            ParsekLog.Info("GameStateRecorder", $"Game state: FundsChanged {delta:+0;-0} ({reason}) → {newFunds:F0}");

            // #445: VesselRollout deducts the vessel cost when the player launches from
            // VAB/SPH onto the launchpad/runway. KSP captures this BEFORE
            // FlightRecorder.CapturePreLaunchResources runs, so the recording-side
            // CreateVesselCostActions sees a near-zero PreLaunchFunds-to-first-point delta
            // and the cost was previously dropped on the floor (especially when the player
            // cancels the rollout without ever starting a recording). Route the deduction
            // through the ledger immediately as a FundsSpending(VesselBuild). A subsequent
            // recording from the same vessel will adopt this action via TryAdoptRolloutAction.
            //
            // Sign/positivity contract: OnVesselRolloutSpending is the authoritative
            // non-positive-cost guard (rejects cost <= 0 with VERBOSE) — we pass the
            // negated delta unconditionally and let the orchestrator decide, so the
            // contract is enforced in one place even if KSP ever fires a refund-style
            // VesselRollout event.
            //
            // IsReplayingActions guard mirrors other career-event handlers — KspStatePatcher
            // replays AddFunds during ledger walks and we must not synthesize new actions.
            //
            // Ordering invariant: this call MUST follow the Emit(...FundsChanged(VesselRollout))
            // above so OnVesselRolloutSpending's ReconcileKscAction can pair the action
            // against the just-emitted event in GameStateStore.
            if (reason == TransactionReasons.VesselRollout && !IsReplayingActions)
                LedgerOrchestrator.OnVesselRolloutSpending(ut, -delta);

            if (reason == TransactionReasons.VesselRecovery && !IsReplayingActions)
                LedgerOrchestrator.OnRecoveryFundsEventRecorded(fundsEvt);
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
            double ut = Planetarium.GetUniversalTime();
            string reasonKey = reason.ToString();
            if (delta > 0.0 && IsScienceSubjectReasonKey(reasonKey))
            {
                latestScienceChangeCapture = new RecentScienceChangeCapture
                {
                    Ut = ut,
                    ReasonKey = reasonKey,
                    Delta = (float)delta,
                    RecordingId = ResolveCurrentRecordingTag(),
                    Valid = true
                };
            }
            else
            {
                latestScienceChangeCapture = default(RecentScienceChangeCapture);
            }

            if (IsScienceDeltaBelowThreshold(delta))
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "science-threshold",
                    $"Ignored ScienceChanged delta={delta:+0.000;-0.000} below threshold={ScienceThreshold:F3}", 5.0);
                return;
            }

            var sciEvt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = reasonKey,
                valueBefore = oldScience,
                valueAfter = newScience
            };
            Emit(ref sciEvt, "ScienceChanged");
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
            if (IsReputationDeltaBelowThreshold(delta))
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "reputation-threshold",
                    $"Ignored ReputationChanged delta={delta:+0.0;-0.0} below threshold={ReputationThreshold:F1}", 5.0);
                return;
            }

            var repEvt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.ReputationChanged,
                key = reason.ToString(),
                valueBefore = oldReputation,
                valueAfter = newReputation
            };
            Emit(ref repEvt, "ReputationChanged");
            ParsekLog.Info("GameStateRecorder", $"Game state: ReputationChanged {delta:+0.0;-0.0} ({reason}) → {newReputation:F1}");
        }

        internal static bool IsReputationDeltaBelowThreshold(float delta)
        {
            float absDelta = Math.Abs(delta);
            return absDelta < ReputationThreshold - ReputationThresholdEpsilon;
        }

        internal static bool IsScienceDeltaBelowThreshold(double delta)
        {
            return Math.Abs(delta) < ScienceThreshold;
        }

        internal static bool IsScienceSubjectReasonKey(string reasonKey)
        {
            return string.Equals(reasonKey, "ScienceTransmission", StringComparison.Ordinal) ||
                   string.Equals(reasonKey, "VesselRecovery", StringComparison.Ordinal);
        }

        internal static bool ShouldUseRecentScienceChangeCapture(
            RecentScienceChangeCapture capture,
            float amount,
            double currentUt,
            string currentRecordingId)
        {
            if (!capture.Valid)
                return false;
            if (capture.Delta <= 0f)
                return false;
            if (!IsScienceSubjectReasonKey(capture.ReasonKey ?? ""))
                return false;
            if (currentUt < capture.Ut)
                return false;
            if (currentUt - capture.Ut > ScienceCaptureMatchWindowSeconds)
                return false;
            if (!string.Equals(
                    capture.RecordingId ?? "",
                    currentRecordingId ?? "",
                    StringComparison.Ordinal))
                return false;

            return Math.Abs(capture.Delta - amount) <= ScienceCaptureMatchDeltaTolerance;
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

            double captureUt = Planetarium.GetUniversalTime();
            string currentRecordingId = ResolveCurrentRecordingTag() ?? "";
            string reasonKey = "";
            string subjectRecordingId = currentRecordingId;
            if (ShouldUseRecentScienceChangeCapture(
                    latestScienceChangeCapture,
                    amount,
                    captureUt,
                    currentRecordingId))
            {
                captureUt = latestScienceChangeCapture.Ut;
                reasonKey = latestScienceChangeCapture.ReasonKey ?? "";
                subjectRecordingId = latestScienceChangeCapture.RecordingId ?? currentRecordingId;
            }
            else if (latestScienceChangeCapture.Valid &&
                     (captureUt < latestScienceChangeCapture.Ut ||
                      captureUt - latestScienceChangeCapture.Ut > ScienceCaptureMatchWindowSeconds ||
                      !string.Equals(
                          latestScienceChangeCapture.RecordingId ?? "",
                          currentRecordingId,
                          StringComparison.Ordinal)))
            {
                latestScienceChangeCapture = default(RecentScienceChangeCapture);
            }
            // Keep a matched capture alive for the rest of the current stock reward burst.
            // Vessel recovery and other multi-subject payouts can fire several
            // OnScienceReceived callbacks after one ScienceChanged capture.

            // Record the cumulative science earned for this subject.
            // Note: subject.science may include Harmony-injected committed science
            // (from ScienceSubjectPatch) if this experiment was previously committed.
            // This is correct — the committed-science cache merges by max value when the
            // eventual ScienceEarning actions are persisted, so repeated captures only
            // ever preserve the highest science earned.
            PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subject.id,
                science = subject.science,
                subjectMaxValue = subject.scienceCap,
                captureUT = captureUt,
                reasonKey = reasonKey,
                recordingId = subjectRecordingId
            });

            ParsekLog.Info("GameStateRecorder",
                $"Science subject captured: {subject.id} amount={amount:F1} total={subject.science:F1} " +
                $"reason='{reasonKey}' ut={captureUt:F1}");
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

            // Route to the testable helper with a live UT from Planetarium. Production
            // is the only call site that touches Unity statics; tests call
            // RegisterPendingMilestoneEvent directly with a literal UT.
            RegisterPendingMilestoneEvent(milestoneId, Planetarium.GetUniversalTime());
        }

        /// <summary>
        /// #443: emit-and-register half of <see cref="OnProgressComplete"/>, extracted as
        /// an internal static so the cached pending-map entry vs. stored-slot identity
        /// match is directly testable without going through the live
        /// <see cref="Planetarium"/>/<see cref="GameEvents"/> plumbing.
        ///
        /// Internal static for testability.
        /// </summary>
        internal static void RegisterPendingMilestoneEvent(string milestoneId, double ut)
        {
            // #400: KSP's reward pipeline is:
            //   subclass handler -> ProgressNode.Complete() -> OnProgressComplete.Fire()
            //                    -> [this handler runs here, rewards NOT yet applied]
            //                    -> subclass calls AwardProgressStandard() -> AwardProgress(..funds, sci, rep..)
            //
            // Because rewards land AFTER this subscriber fires, we emit the event with
            // zeros as defaults and rely on the AwardProgressPatch Harmony postfix to
            // update the detail in place once it has the real reward values. For non-
            // career modes the zeros are correct (no rewards apply).
            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = milestoneId,
                detail = BuildMilestoneDetail(0.0, 0f, 0.0)
            };
            Emit(ref evt, "MilestoneAchieved");

            // Emit/AddEvent take `ref GameStateEvent`, so `evt` here already carries the
            // final identity fields — no mirror needed. The cached copy below is
            // guaranteed to match the stored slot.
            //
            // #443: Tag node.Id -> event association (was ProgressNode reference pre-#443).
            // The key is the qualified milestone id (e.g. "Kerbin/Landing"), which both
            // OnProgressComplete and the AwardProgress postfix derive deterministically
            // via QualifyMilestoneId(node). Re-keying by string removes any future
            // exposure to ProgressNode instance-aliasing should KSP ever rebuild nodes
            // mid-Complete().
            PendingMilestoneEventById[milestoneId] = evt;

            ParsekLog.Info("GameStateRecorder",
                $"Game state: MilestoneAchieved '{milestoneId}' (awaiting reward enrichment)");

            // Milestones can fire at KSC (e.g., facility-related) or in flight.
            // Write directly to ledger when no recording owner exists.
            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so untagged
            // pre-recording FLIGHT milestones reach the ledger too.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
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
        /// Short-lived map of qualified milestone id -> pending MilestoneAchieved event.
        /// Populated by <see cref="OnProgressComplete"/> when the event is emitted with
        /// zero rewards, and read by <see cref="EnrichPendingMilestoneRewards"/> (called
        /// from the Harmony postfix on <c>ProgressNode.AwardProgress</c>) to update the
        /// detail string in place with the real funds/rep/sci values from the award
        /// parameters.
        ///
        /// #443: Re-keyed from <c>ProgressNode</c> reference to qualified id string. The
        /// id is derived deterministically by <see cref="QualifyMilestoneId"/> at both the
        /// emit and enrich call sites, so the lookup no longer depends on KSP handing the
        /// same instance to both code paths. The cached <see cref="GameStateEvent"/> value
        /// keeps the same identity fields as the stored slot, so the later
        /// <see cref="GameStateStore.UpdateEventDetail"/> call can patch the correct row
        /// without any epoch mirror.
        ///
        /// Entries are removed immediately after enrichment. Stale entries (if AwardProgress
        /// never fires for a node) don't survive beyond the save session — Subscribe(),
        /// Unsubscribe(), and ResetForTesting() all clear the map before a new session can
        /// route rewards through it.
        /// </summary>
        internal static readonly Dictionary<string, GameStateEvent> PendingMilestoneEventById
            = new Dictionary<string, GameStateEvent>(StringComparer.Ordinal);

        internal static void ClearPendingMilestoneEvents(string reason)
        {
            int cleared = PendingMilestoneEventById.Count;
            if (cleared <= 0)
                return;

            PendingMilestoneEventById.Clear();
            ParsekLog.Verbose("GameStateRecorder",
                $"Cleared {cleared} pending milestone reward entr{(cleared == 1 ? "y" : "ies")} ({reason ?? "unspecified"})");
        }

        /// <summary>
        /// Called from the Harmony postfix on <c>ProgressNode.AwardProgress</c>. Looks
        /// up the pending MilestoneAchieved event for the given node (by its qualified
        /// id) and updates its detail string with the real funds/rep/sci values. If no
        /// pending event is found (e.g., non-career mode where the subscriber didn't run),
        /// this is a no-op. Also updates any matching ledger action that was already
        /// written via <see cref="LedgerOrchestrator.OnKscSpending"/> — otherwise KSC
        /// milestones would have zero rewards in the ledger.
        /// Internal static for testability.
        /// </summary>
        internal static void EnrichPendingMilestoneRewards(
            ProgressNode node, double funds, float rep, double sci)
        {
            if (node == null) return;
            string milestoneId = QualifyMilestoneId(node);
            if (string.IsNullOrEmpty(milestoneId))
            {
                ParsekLog.Warn("GameStateRecorder",
                    "EnrichPendingMilestoneRewards: empty milestone id — cannot enrich");
                return;
            }
            if (!PendingMilestoneEventById.TryGetValue(milestoneId, out var evt))
            {
                // #443: promoted from Verbose to Warn so the failure surfaces in default
                // log settings. A miss here means the AwardProgress postfix ran without a
                // matching OnProgressComplete-emitted pending event — usually a sign that
                // either the dict was cleared between emit and enrich, or that the routing
                // branch in ProgressRewardPatch.RoutePostfix sent us here incorrectly. Carry
                // enough context (id, expected rewards, pending-map size) to triage.
                ParsekLog.Warn("GameStateRecorder",
                    $"EnrichPendingMilestoneRewards: no pending event for milestone id '{milestoneId}' " +
                    $"(node.Id='{node.Id ?? "<null>"}', funds={funds:F0} rep={rep:F1} sci={sci:F1}, " +
                    $"pendingMapSize={PendingMilestoneEventById.Count}) — skip");
                return;
            }

            // Build the new detail string and patch the store's copy in place.
            // GameStateEvent is a value type; the cached `evt` is a separate copy that
            // we must also update for the test hooks and downstream observers.
            string newDetail = BuildMilestoneDetail(funds, rep, sci);
            bool storeUpdated = GameStateStore.UpdateEventDetail(evt, newDetail);
            if (!storeUpdated)
            {
                // #443: defensive — UpdateEventDetail returning false used to be silent.
                // Surface it so a future regression in the cached-vs-stored slot match
                // is diagnosable from a default-level log. The trailing
                // happy-path "Milestone enriched" Info is intentionally suppressed in this
                // branch (logged inside the `else` below) so an operator never sees the
                // contradicting "store had no matching event" Warn and "Milestone enriched"
                // Info for the same call.
                ParsekLog.Warn("GameStateRecorder",
                    $"EnrichPendingMilestoneRewards: store had no matching event for " +
                    $"'{evt.key}' ut={evt.ut:F1} tag='{evt.recordingId ?? ""}' — detail not patched");
            }
            evt.detail = newDetail;
            PendingMilestoneEventById.Remove(milestoneId);

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

            // #443 review: only print the happy-path Info when the store side actually
            // got patched. The earlier shape printed it unconditionally, so on a
            // store-miss the operator saw a contradicting Warn + Info pair for the same
            // call. On the miss branch we leave the Warn above as the sole signal and
            // demote the in-memory-only success line to Verbose for full-fidelity replay.
            if (storeUpdated)
            {
                ParsekLog.Info("GameStateRecorder",
                    $"Milestone enriched: '{evt.key}' funds={funds:F0} rep={rep:F1} sci={sci:F1}");
            }
            else
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"Milestone enriched (ledger only, store skipped): '{evt.key}' " +
                    $"funds={funds:F0} rep={rep:F1} sci={sci:F1}");
            }
        }

        /// <summary>
        /// #442: emits a fully-populated <see cref="GameStateEventType.MilestoneAchieved"/>
        /// event for progress nodes whose subclass calls <c>AwardProgress</c> directly
        /// without going through <c>OnProgressComplete</c> (e.g. <c>RecordsSpeed</c>,
        /// <c>RecordsAltitude</c>, <c>RecordsDistance</c>, <c>RecordsDepth</c>). Without
        /// this path the world-record FundsChanged(Progression) deltas are dropped wholesale
        /// by <see cref="GameStateEventConverter"/>'s drop-rule and the rewards never reach
        /// the ledger.
        ///
        /// Called from the Harmony postfix on <c>ProgressNode.AwardProgress</c> only when
        /// no entry exists in <see cref="PendingMilestoneEventById"/> — i.e. when the
        /// usual two-phase emit-then-enrich path did not run. Bypasses the enrichment-map
        /// indirection entirely: rewards arrive as method parameters and are baked into
        /// the event detail directly.
        ///
        /// <paramref name="ut"/> is supplied by the caller — production passes
        /// <see cref="Planetarium.GetUniversalTime"/> from the patch site (which has Unity
        /// statics live), unit tests pass a literal so they don't NRE on Planetarium.
        /// Internal static for testability.
        /// </summary>
        internal static void EmitStandaloneProgressReward(
            ProgressNode node, double funds, float rep, double sci, double ut)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "EmitStandaloneProgressReward: suppressed during action replay");
                return;
            }
            if (node == null)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "EmitStandaloneProgressReward: null node — skipped");
                return;
            }

            string milestoneId = QualifyMilestoneId(node);
            if (string.IsNullOrEmpty(milestoneId))
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "EmitStandaloneProgressReward: empty node Id — skipped");
                return;
            }

            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = milestoneId,
                detail = BuildMilestoneDetail(funds, rep, sci)
            };
            Emit(ref evt, "MilestoneAchievedStandalone");

            ParsekLog.Info("GameStateRecorder",
                $"Game state: MilestoneAchieved (standalone) '{milestoneId}' " +
                $"funds={funds:F0} rep={rep:F1} sci={sci:F1}");

            // Mirror the OnProgressComplete KSC-forwarding path so world-record rewards
            // earned outside flight (rare, but possible — e.g. RecordsAltitude on a sub-
            // orbital craft already reverted) still reach the ledger immediately.
            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so untagged
            // pre-recording FLIGHT standalone milestone awards reach the ledger too.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
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

        #region Strategy Lifecycle (#439 Phase A)

        /// <summary>
        /// Called from the Harmony postfix on <c>Strategies.Strategy.Activate</c> when
        /// <c>__result == true</c> (stock returns false on a CanBeActivated miss). Emits a
        /// <see cref="GameStateEventType.StrategyActivated"/> event carrying the strategy's
        /// configured setup costs; downstream <see cref="GameStateEventConverter"/> parses
        /// the detail and the ledger's <see cref="LedgerOrchestrator.ClassifyAction"/>
        /// reconciles the funds setup cost against the paired
        /// <c>FundsChanged(StrategySetup)</c> event KSP fires inside <c>Activate()</c>.
        ///
        /// Respects <see cref="IsReplayingActions"/> so recalculation replays stay silent.
        /// Internal static for testability — the Harmony postfix is the only production
        /// call site.
        /// </summary>
        internal static void OnStrategyActivated(Strategies.Strategy strategy)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "OnStrategyActivated: suppressed during action replay");
                return;
            }
            if (strategy == null)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "OnStrategyActivated: null strategy - skipped");
                return;
            }
            if (strategy.Config == null)
            {
                ParsekLog.Warn("GameStateRecorder",
                    "OnStrategyActivated: strategy.Config is null - skipped");
                return;
            }

            string key = strategy.Config.Name ?? "";
            string title = strategy.Title ?? "";
            string dept = strategy.DepartmentName ?? "";
            float factor = strategy.Factor;
            float setupFunds = strategy.InitialCostFunds;
            float setupSci = strategy.InitialCostScience;
            float setupRep = strategy.InitialCostReputation;
            StrategyResource sourceResource;
            StrategyResource targetResource;
            if (!TryExtractStrategyResourceFlow(strategy, out sourceResource, out targetResource))
            {
                sourceResource = StrategyResource.Funds;
                targetResource = StrategyResource.Funds;
            }

            var evt = new GameStateEvent
            {
                ut = Planetarium.GetUniversalTime(),
                eventType = GameStateEventType.StrategyActivated,
                key = key,
                detail = BuildStrategyActivateDetail(title, dept, factor,
                    setupFunds, setupSci, setupRep,
                    sourceResource: sourceResource, targetResource: targetResource)
            };
            Emit(ref evt, "StrategyActivated");

            ParsekLog.Info("GameStateRecorder",
                $"Game state: StrategyActivated '{key}' title='{title}' dept='{dept}' " +
                $"factor={factor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"setupFunds={setupFunds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"setupSci={setupSci.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"setupRep={setupRep.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");

            // KSC-side forwarding mirror: StrategyActivate actions with a non-zero setup
            // cost need to land on the ledger immediately when the player activates from
            // the Administration building (i.e. outside flight). The commit-time
            // ConvertEvents path will also pick up flight-scope strategies. The
            // ShouldForwardDirectLedgerEvent gate suppresses tagged teardown events
            // while still letting untagged pre-recording FLIGHT events (no live
            // recorder) reach the ledger.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Called from the Harmony postfix on <c>Strategies.Strategy.Deactivate</c> when
        /// <c>__result == true</c>. Emits <see cref="GameStateEventType.StrategyDeactivated"/>.
        /// Stock Deactivate has no resource cost, so the classifier maps this to
        /// <see cref="KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact"/>.
        /// Internal static for testability.
        /// </summary>
        internal static void OnStrategyDeactivated(Strategies.Strategy strategy)
        {
            if (IsReplayingActions)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "OnStrategyDeactivated: suppressed during action replay");
                return;
            }
            if (strategy == null)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    "OnStrategyDeactivated: null strategy - skipped");
                return;
            }
            if (strategy.Config == null)
            {
                ParsekLog.Warn("GameStateRecorder",
                    "OnStrategyDeactivated: strategy.Config is null - skipped");
                return;
            }

            string key = strategy.Config.Name ?? "";
            string title = strategy.Title ?? "";
            string dept = strategy.DepartmentName ?? "";
            float factor = strategy.Factor;
            double now = Planetarium.GetUniversalTime();
            double activeDurationSec = now - strategy.DateActivated;
            if (activeDurationSec < 0) activeDurationSec = 0;

            var evt = new GameStateEvent
            {
                ut = now,
                eventType = GameStateEventType.StrategyDeactivated,
                key = key,
                detail = BuildStrategyDeactivateDetail(title, dept, factor, activeDurationSec)
            };
            Emit(ref evt, "StrategyDeactivated");

            ParsekLog.Info("GameStateRecorder",
                $"Game state: StrategyDeactivated '{key}' title='{title}' dept='{dept}' " +
                $"factor={factor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"activeDurationSec={activeDurationSec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");

            // Mirror the KSC-forwarding path even though StrategyDeactivate is a
            // NoResourceImpact action — the ledger still needs the StrategyDeactivate row
            // so StrategiesModule can pair activate/deactivate during the walk.
            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so untagged
            // pre-recording FLIGHT strategy deactivations reach the ledger too.
            if (ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(evt);
        }

        /// <summary>
        /// Pure: builds the semicolon-separated detail string for a StrategyActivated
        /// event. Format:
        /// <c>title=&lt;t&gt;;dept=&lt;d&gt;;factor=&lt;f&gt;;source=&lt;src&gt;;target=&lt;tgt&gt;;setupFunds=&lt;sf&gt;;setupSci=&lt;ss&gt;;setupRep=&lt;sr&gt;</c>.
        /// Numerics serialized with InvariantCulture "R" (round-trip) so comma-locale
        /// hosts do not break the converter-side parse.
        /// Internal static for direct unit testing without a KSP runtime.
        /// </summary>
        internal static string BuildStrategyActivateDetail(
            string title, string dept, float factor,
            float setupFunds, float setupSci, float setupRep,
            StrategyResource sourceResource = StrategyResource.Funds,
            StrategyResource targetResource = StrategyResource.Funds)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return $"title={title ?? ""};" +
                   $"dept={dept ?? ""};" +
                   $"factor={factor.ToString("R", ic)};" +
                   $"source={sourceResource};" +
                   $"target={targetResource};" +
                   $"setupFunds={setupFunds.ToString("R", ic)};" +
                   $"setupSci={setupSci.ToString("R", ic)};" +
                   $"setupRep={setupRep.ToString("R", ic)}";
        }

        /// <summary>
        /// Pure: builds the semicolon-separated detail string for a StrategyDeactivated
        /// event. Format: <c>title=&lt;t&gt;;dept=&lt;d&gt;;factor=&lt;f&gt;;activeDurationSec=&lt;d&gt;</c>.
        /// Internal static for direct unit testing.
        /// </summary>
        internal static string BuildStrategyDeactivateDetail(
            string title, string dept, float factor, double activeDurationSec)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return $"title={title ?? ""};" +
                   $"dept={dept ?? ""};" +
                   $"factor={factor.ToString("R", ic)};" +
                   $"activeDurationSec={activeDurationSec.ToString("R", ic)}";
        }

        internal static bool TryExtractStrategyResourceFlow(
            Strategies.Strategy strategy,
            out StrategyResource sourceResource,
            out StrategyResource targetResource)
        {
            sourceResource = StrategyResource.Funds;
            targetResource = StrategyResource.Funds;

            if (strategy == null || strategy.Effects == null)
                return false;

            for (int i = 0; i < strategy.Effects.Count; i++)
            {
                object effect = strategy.Effects[i];
                if (effect == null)
                    continue;

                if (TryExtractCurrencyConverterFlow(effect, out sourceResource, out targetResource))
                    return true;

                if (TryExtractCurrencyOperationFlow(effect, out sourceResource))
                {
                    targetResource = sourceResource;
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractCurrencyConverterFlow(
            object effect,
            out StrategyResource sourceResource,
            out StrategyResource targetResource)
        {
            sourceResource = StrategyResource.Funds;
            targetResource = StrategyResource.Funds;

            Type effectType = effect.GetType();
            if (!string.Equals(effectType.FullName, "Strategies.Effects.CurrencyConverter",
                    StringComparison.Ordinal))
                return false;

            var inputField = effectType.GetField("input",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var outputField = effectType.GetField("output",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (inputField == null || outputField == null)
                return false;

            return TryParseStrategyCurrency(inputField.GetValue(effect), out sourceResource)
                && TryParseStrategyCurrency(outputField.GetValue(effect), out targetResource);
        }

        private static bool TryExtractCurrencyOperationFlow(
            object effect,
            out StrategyResource resource)
        {
            resource = StrategyResource.Funds;

            Type effectType = effect.GetType();
            if (!string.Equals(effectType.FullName, "Strategies.Effects.CurrencyOperation",
                    StringComparison.Ordinal))
                return false;

            var currencyField = effectType.GetField("currency",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return currencyField != null
                && TryParseStrategyCurrency(currencyField.GetValue(effect), out resource);
        }

        private static bool TryParseStrategyCurrency(
            object rawCurrency,
            out StrategyResource resource)
        {
            resource = StrategyResource.Funds;
            if (rawCurrency == null)
                return false;

            int value;
            try
            {
                value = Convert.ToInt32(rawCurrency, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }

            switch (value)
            {
                case 0:
                    resource = StrategyResource.Funds;
                    return true;
                case 1:
                    resource = StrategyResource.Science;
                    return true;
                case 2:
                    resource = StrategyResource.Reputation;
                    return true;
                default:
                    return false;
            }
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
                            Emit(ref evt, eventType.ToString());
                            eventsEmitted++;
                            ParsekLog.Info("GameStateRecorder", $"Game state: {eventType} '{kvp.Key}' {cachedLevel:F2} → {currentLevel:F2}");

                            // #553 follow-up: gate on ShouldForwardDirectLedgerEvent so
                            // untagged pre-recording FLIGHT facility upgrades reach the
                            // ledger too. Only FacilityUpgraded forwards (downgrades are
                            // informational).
                            if (eventType == GameStateEventType.FacilityUpgraded
                                && ShouldForwardDirectLedgerEvent(evt.recordingId, HasLiveRecorder()))
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
                            Emit(ref bldEvt, eventType.ToString());
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
