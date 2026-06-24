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
    internal partial class GameStateRecorder
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
            ClearContractCompletionDedup("ResetForTesting");
            RecoveryPayoutContextStore.ResetForTesting();
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
        private readonly GameStateFacilityRecorder facilityRecorder;

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

        internal const double FundsThreshold = 100.0;
        private const double ScienceThreshold = 0.001;
        private const double ScienceCaptureMatchWindowSeconds = 5.0;
        private const float ScienceCaptureMatchDeltaTolerance = 0.05f;
        private const float ReputationThreshold = 1.0f;
        private const float ReputationThresholdEpsilon = 0.001f;

        internal GameStateRecorder()
        {
            facilityRecorder = new GameStateFacilityRecorder(this);
        }

        #region Subscription Management

        internal void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            pendingCrewEvents.Clear();
            latestScienceChangeCapture = default(RecentScienceChangeCapture);
            ClearPendingMilestoneEvents("Subscribe");
            ClearContractCompletionDedup("Subscribe");

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
            ClearContractCompletionDedup("Unsubscribe");

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
            string detail = null;
            if (reason == TransactionReasons.VesselRecovery)
                detail = BuildVesselRecoveryFundsDetail(ut, delta);

            var fundsEvt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = reason.ToString(),
                detail = detail,
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

            // Bail-Out Grant (stock CurrencyExchanger) credits funds directly under
            // TransactionReasons.StrategyOutput with no recording owner and no other
            // capture channel. Forward it straight to the ledger so the recalc preserves
            // the grant instead of clobbering it. ShouldForwardDirectLedgerEvent skips the
            // write when a live recorder owns the event (it then flows through the
            // commit-time ConvertEvents path). See
            // fix-bailout-grant-currency-exchange-capture.md.
            if (reason == TransactionReasons.StrategyOutput &&
                ShouldForwardDirectLedgerEvent(fundsEvt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(fundsEvt);
        }

        internal static string BuildVesselRecoveryFundsDetail(double ut)
        {
            return BuildVesselRecoveryFundsDetail(ut, double.NaN);
        }

        internal static string BuildVesselRecoveryFundsDetail(double ut, double fundsDelta)
        {
            return RecoveryPayoutContextStore.TryBuildFundsEventDetail(ut, fundsDelta, out string detail)
                ? detail
                : null;
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

            // Bail-Out Grant (stock CurrencyExchanger) subtracts reputation directly under
            // TransactionReasons.StrategyInput with no recording owner and no other capture
            // channel. Forward it straight to the ledger so the recalc preserves the spent
            // reputation instead of refunding it. ShouldForwardDirectLedgerEvent skips the
            // write when a live recorder owns the event. See
            // fix-bailout-grant-currency-exchange-capture.md.
            if (reason == TransactionReasons.StrategyInput &&
                ShouldForwardDirectLedgerEvent(repEvt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(repEvt);
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
            var pendingSubject = new PendingScienceSubject
            {
                subjectId = subject.id,
                science = subject.science,
                subjectMaxValue = subject.scienceCap,
                captureUT = captureUt,
                reasonKey = reasonKey,
                recordingId = subjectRecordingId
            };

            bool hasLiveRecorder = HasLiveRecorder();
            bool hasActiveUncommittedTree = HasActiveUncommittedTree();
            bool directLedgerHandled = false;
            if (ShouldForwardDirectScienceSubject(
                    pendingSubject.recordingId,
                    hasLiveRecorder,
                    hasActiveUncommittedTree))
            {
                string vesselName = vessel != null ? vessel.vesselName : null;
                directLedgerHandled = LedgerOrchestrator.TryRecordKscScienceSubject(
                    pendingSubject,
                    vesselName);
            }

            if (!directLedgerHandled)
            {
                if (ShouldForwardDirectLedgerEvent(pendingSubject.recordingId, hasLiveRecorder) &&
                    hasActiveUncommittedTree)
                {
                    ParsekLog.Verbose("GameStateRecorder",
                        $"OnScienceReceived: retained unowned science subject '{subject.id}' " +
                        "because an uncommitted recording tree is active");
                }
                PendingScienceSubjects.Add(pendingSubject);
            }

            ParsekLog.Info("GameStateRecorder",
                $"Science subject captured: {subject.id} amount={amount:F1} total={subject.science:F1} " +
                $"reason='{reasonKey}' ut={captureUt:F1} tag='{subjectRecordingId}' " +
                $"directLedger={directLedgerHandled}");
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
            facilityRecorder.SeedFacilityCacheFromCurrentState();
        }

        /// <summary>
        /// Polls current facility/building state and emits events for any changes
        /// since the cached state. Called on Subscribe() after cache is seeded.
        /// </summary>
        internal void PollFacilityState()
        {
            facilityRecorder.PollFacilityState();
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
            return GameStateFacilityRecorder.CheckFacilityTransitions(cached, current, ut);
        }

        /// <summary>
        /// Checks the building transition logic without Unity dependencies.
        /// Returns events that would be emitted for the given state change.
        /// </summary>
        internal static List<GameStateEvent> CheckBuildingTransitions(
            Dictionary<string, bool> cached, Dictionary<string, bool> current, double ut)
        {
            return GameStateFacilityRecorder.CheckBuildingTransitions(cached, current, ut);
        }

        #endregion

        internal void EmitFacilityEvent(ref GameStateEvent evt, string source)
        {
            Emit(ref evt, source);
        }

        internal bool ShouldForwardFacilityLedgerEvent(string recordingId)
        {
            return ShouldForwardDirectLedgerEvent(recordingId, HasLiveRecorder());
        }
    }
}
