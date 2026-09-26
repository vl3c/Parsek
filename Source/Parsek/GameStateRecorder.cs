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
        /// the part's <c>entryCost</c> (not its rollout/build <c>cost</c>), except for an
        /// identical part stock buys alongside it (<paramref name="costsFunds"/> false),
        /// which is free. Internal static for unit test coverage.
        /// </summary>
        internal static GameStateEvent CreatePartPurchasedEvent(
            string partName,
            float entryCost,
            bool bypassEntryPurchaseAfterResearch,
            double ut,
            double currentFunds,
            bool costsFunds = true)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            float chargedCost = ComputePartPurchaseChargedCost(
                entryCost, costsFunds, bypassEntryPurchaseAfterResearch);
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

            // Query-family strategy door (STRATEGY-SCIENCE-CONVERSION-LEAK /
            // STRATEGY-FUNDS-YIELD-DRIFT). OnCurrencyModified - NOT
            // OnCurrencyModifierQuery, which stock's 33 display sites fire through
            // CurrencyModifierQuery.RunQuery without moving any balance. Instance
            // method, so EventData.Add gets a real target (a static method handed to
            // EventData.Add throws - see the static-GameEvent-handler NRE trap).
            GameEvents.Modifiers.OnCurrencyModified.Add(OnCurrencyModified);

            // Science subjects (per-experiment tracking for duplication prevention)
            GameEvents.OnScienceRecieved.Add(OnScienceReceived);

            // Progress milestones
            GameEvents.OnProgressComplete.Add(OnProgressComplete);

            // Kerbal experience: fires immediately AFTER VesselRecovery archives each crew
            // member's flight log into their career log (decompile-verified ordering).
            GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessingForExperience);

            // Facility upgrades (event-driven). The scene-load PollFacilityState below only
            // catches upgrades across a scene change; subscribing to OnKSCFacilityUpgrading
            // captures an in-scene upgrade (UI or seam) immediately, without a seeded
            // baseline. See GameStateFacilityRecorder.OnFacilityUpgrading.
            GameEvents.OnKSCFacilityUpgrading.Add(OnFacilityUpgrading);

            // KSC building destruction / repair (event-driven, at the moment it happens).
            // Both events fire synchronously inside DestructibleBuilding.Demolish / Repair;
            // the later Collapsed / Repaired events wait on an animation coroutine. The
            // ResetStructures hook catches the silent repair inside a facility upgrade.
            // See GameStateFacilityRecorder.RecordBuildingTransition.
            GameEvents.OnKSCStructureCollapsing.Add(OnStructureCollapsing);
            GameEvents.OnKSCStructureRepairing.Add(OnStructureRepairing);
            FacilityRepairCapture.StructuresReset += OnStructuresReset;

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

            // Query-family strategy door (symmetric with the Subscribe side).
            GameEvents.Modifiers.OnCurrencyModified.Remove(OnCurrencyModified);

            // Science subjects
            GameEvents.OnScienceRecieved.Remove(OnScienceReceived);

            // Progress milestones
            GameEvents.OnProgressComplete.Remove(OnProgressComplete);

            // Kerbal experience
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessingForExperience);

            // Facility upgrades (event-driven)
            GameEvents.OnKSCFacilityUpgrading.Remove(OnFacilityUpgrading);

            // KSC building destruction / repair
            GameEvents.OnKSCStructureCollapsing.Remove(OnStructureCollapsing);
            GameEvents.OnKSCStructureRepairing.Remove(OnStructureRepairing);
            FacilityRepairCapture.StructuresReset -= OnStructuresReset;

            ParsekLog.Info("GameStateRecorder", "GameStateRecorder unsubscribed");
        }

        #endregion

        #region Resource Handlers

        /// <summary>
        /// True while any resource baseline is still unseeded (NaN).
        ///
        /// <para>
        /// An unseeded baseline is not inert: <see cref="OnScienceChanged"/> /
        /// <see cref="OnFundsChanged"/> / <see cref="OnReputationChanged"/> all stamp the new
        /// value and then <c>return</c> on <c>IsNaN(old)</c>, so the FIRST real change of the
        /// scene is silently consumed as the primer - no event, no log, and (for science) no
        /// reason capture for the subject that caused it. See
        /// <see cref="SeedResourceState"/> for why a baseline can start out NaN.
        /// </para>
        /// </summary>
        internal bool HasUnseededResourceBaselines =>
            double.IsNaN(lastFunds) || double.IsNaN(lastScience) || double.IsNaN(lastReputation);

        /// <summary>
        /// Seeds any still-unseeded resource baseline from the live currency singletons, and
        /// reports whether all three are now seeded.
        ///
        /// <para>
        /// <b>Fill-only-NaN, so it is safe to call repeatedly.</b> A baseline that is already
        /// seeded is left alone; overwriting one would silently swallow whatever changed since
        /// it was taken.
        /// </para>
        ///
        /// <para>
        /// <b>Why a baseline can start out NaN</b> (CAREER-TRANSMIT-SCIENCE-EMITS-NO-CORROBORATING-EVENT):
        /// a fresh recorder is constructed and subscribed from <c>ParsekScenario.OnLoad</c> on
        /// every scene load, and KSP calls OnLoad from the middle of
        /// <c>ScenarioRunner.LoadModules</c> - so the currency ScenarioModules that happen to
        /// sit after Parsek's in the save can still be null here. On the flight that found
        /// this, <c>ResearchAndDevelopment.Instance</c> was null at the space-centre subscribe,
        /// <c>lastScience</c> stayed NaN, and the recovered Mystery Goo's +3.6 science credit
        /// was eaten as the primer: no <c>ScienceChanged(VesselRecovery)</c> event, so the
        /// subject reached the ledger with an empty reason, fell back to
        /// <c>method=Transmitted</c>, and the post-walk reconcile could never match it.
        /// <c>ParsekScenario</c> re-runs this once the singletons appear.
        /// </para>
        /// </summary>
        internal bool SeedResourceState()
        {
            lastFunds = SeedBaselineIfUnseeded(
                lastFunds,
                Funding.Instance != null,
                Funding.Instance != null ? Funding.Instance.Funds : 0.0);
            lastScience = SeedBaselineIfUnseeded(
                lastScience,
                ResearchAndDevelopment.Instance != null,
                ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0);
            lastReputation = (float)SeedBaselineIfUnseeded(
                lastReputation,
                Reputation.Instance != null,
                Reputation.Instance != null ? Reputation.Instance.reputation : 0.0);

            return !HasUnseededResourceBaselines;
        }

        /// <summary>
        /// Pure baseline-seeding decision: take the singleton's value ONLY when the baseline is
        /// still unseeded (NaN) AND the singleton exists.
        ///
        /// <para>
        /// Both halves matter. Overwriting a seeded baseline would discard the change history
        /// between the old baseline and now - the very swallow this seeding exists to prevent -
        /// and a value read from an absent singleton would be a fabricated zero, which for
        /// funds or science is a large false delta on the next real change.
        /// </para>
        ///
        /// <para>
        /// A genuinely-zero pool (fresh career science, a career that started at reputation 0)
        /// seeds to 0 and is then SEEDED, not unseeded: zero is a real baseline, and only NaN
        /// means "never read".
        /// </para>
        /// </summary>
        internal static double SeedBaselineIfUnseeded(
            double currentBaseline,
            bool singletonPresent,
            double singletonValue)
        {
            if (!double.IsNaN(currentBaseline))
                return currentBaseline;
            return singletonPresent ? singletonValue : currentBaseline;
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

            // STRATEGY-ECHO-CAPTURE-WIPE: the threshold check runs FIRST, so a
            // below-threshold ScienceChanged - notably the zero-delta trailing echo a
            // stock CurrencyConverter strategy produces after it has already taken its
            // share - leaves latestScienceChangeCapture untouched instead of wiping it.
            // Before this ordering, every recovery award landing while a query-family
            // strategy was active lost the VesselRecovery reasonKey the capture carried,
            // so LedgerOrchestrator.ResolveKscScienceRecordingId fell back to
            // method=Transmitted and the science kept its credit but lost its recording
            // attribution. A REAL negative delta still clears the capture below (that
            // cache is for POSITIVE subject awards and a stale one would mis-attribute).
            // Accepted corollary of the same ordering: a below-threshold POSITIVE subject
            // award no longer SETS the capture either. Its magnitude is under the ledger's
            // own science floor, so there is nothing for the attribution it would carry to
            // attribute; a sub-milli-point award is not worth a capture.
            if (IsScienceDeltaBelowThreshold(delta))
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "science-threshold",
                    $"Ignored ScienceChanged delta={delta:+0.000;-0.000} below threshold={ScienceThreshold:F3}", 5.0);
                return;
            }

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

            // Patents Licensing (stock CurrencyExchanger / CurrencyConverter,
            // researchIPsellout) subtracts science directly under
            // TransactionReasons.StrategyInput with no recording owner and no other
            // capture channel - the strategy's own InitialCostScience setup charge is
            // separate and rides StrategyActivate. Forward it straight to the ledger so
            // the recalc preserves the traded-away science instead of refunding it.
            // ShouldForwardDirectLedgerEvent skips the write when a live recorder owns
            // the event (it then flows through the commit-time ConvertEvents path).
            // Third leg of the carve-out pair at OnFundsChanged (StrategyOutput) and
            // OnReputationChanged (StrategyInput). See
            // fix-bailout-grant-currency-exchange-capture.md and the
            // STRATEGY-SCIENCE-CONVERSION-LEAK entry in docs/dev/todo-and-known-bugs.md.
            //
            // Ordering invariant: this call MUST follow the Emit(...ScienceChanged) above
            // so the event is IN GameStateStore before OnKscSpending's downstream reads.
            // NOT for ReconcileKscAction pairing - ClassifyAction returns Transformed for
            // StrategyScienceDebit and the reconcile short-circuits before any leg is
            // matched, so no pairing ever happens. What DOES read the store synchronously
            // inside this call is OnKscSpending's recalc tail:
            // ComputePendingUncommittedStrategyScienceDebit (which must see this event to
            // net it against the row being written) and ComputeEarningsWindowStoreDeltas.
            //
            // Two live traps, both intentional: this method early-returns above when
            // |delta| < ScienceThreshold (0.001), so a sub-milli-point exchange is not
            // captured at all; and a REAL negative delta still CLEARS
            // latestScienceChangeCapture. The clear is kept (that cache is for POSITIVE
            // subject awards and a stale capture would mis-attribute one), but it is
            // not free: an exchange firing between a recovery's ScienceChanged credit
            // and its OnScienceReceived callbacks drops the reasonKey the capture
            // carried, so LedgerOrchestrator.ResolveKscScienceRecordingId no longer
            // sees VesselRecovery and the recovered science loses its recording
            // attribution (it stays credited, untagged). Recorded as a residual on the
            // STRATEGY-SCIENCE-CONVERSION-LEAK entry in docs/dev/todo-and-known-bugs.md.
            // The far more common ZERO-delta echo no longer does this - see
            // STRATEGY-ECHO-CAPTURE-WIPE and the reordered block above.
            if (reason == TransactionReasons.StrategyInput &&
                ShouldForwardDirectLedgerEvent(sciEvt.recordingId, HasLiveRecorder()))
                LedgerOrchestrator.OnKscSpending(sciEvt);
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

        /// <summary>
        /// The QUERY-FAMILY strategy door (STRATEGY-SCIENCE-CONVERSION-LEAK /
        /// STRATEGY-FUNDS-YIELD-DRIFT). <c>Strategies.Effects.CurrencyConverter</c>
        /// (Patents Licensing + 7 siblings) and <c>CurrencyOperation</c> mutate a
        /// <c>CurrencyModifierQuery</c> in place; <c>AddScience</c>/<c>AddFunds</c> then
        /// fire their Changed events ALREADY NET, under the ORIGINAL reason, so the
        /// three reason-keyed doors above cannot see the strategy's share at all.
        /// This handler reads it from the query itself.
        ///
        /// <para>Bound to <c>OnCurrencyModified</c>, which rides an actual <c>Add*</c> -
        /// NOT <c>OnCurrencyModifierQuery</c>, which stock's 33
        /// <c>CurrencyModifierQuery.RunQuery</c> display sites fire without moving a
        /// balance.</para>
        ///
        /// <para>Stands down on the same two flags every other recorder door does, so
        /// timeline replay, the in-game tests' <c>SuppressionGuard.Resources()</c>
        /// restores, and <c>KspStatePatcher</c>'s own ledger-walk writes cannot feed the
        /// ledger back into itself.</para>
        /// </summary>
        private void OnCurrencyModified(CurrencyModifierQuery qry)
        {
            if (qry == null)
                return;

            string standDownReason;
            if (StrategyConversionCapture.ShouldStandDown(
                    SuppressResourceEvents, IsReplayingActions, out standDownReason))
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-currency-modified",
                    $"Suppressed OnCurrencyModified capture ({standDownReason})", 5.0);
                return;
            }

            StrategyConversionQuery snapshot;
            try
            {
                snapshot = new StrategyConversionQuery
                {
                    InputFunds = qry.GetInput(Currency.Funds),
                    DeltaFunds = qry.GetEffectDelta(Currency.Funds),
                    InputScience = qry.GetInput(Currency.Science),
                    DeltaScience = qry.GetEffectDelta(Currency.Science),
                    InputReputation = qry.GetInput(Currency.Reputation),
                    DeltaReputation = qry.GetEffectDelta(Currency.Reputation),
                    Reason = qry.reason.ToString()
                };
            }
            catch (Exception ex)
            {
                // Never let a modded query implementation take down a stock Add* call.
                ParsekLog.Warn("GameStateRecorder",
                    $"OnCurrencyModified: reading the query threw, capture skipped: {ex}");
                return;
            }

            var legs = StrategyConversionCapture.EvaluateLegs(snapshot);
            if (legs.Count == 0)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "currency-modified-noop",
                    $"OnCurrencyModified: no capture-worthy leg - " +
                    $"{StrategyConversionCapture.FormatQuery(snapshot, 0)}", 5.0);
                return;
            }

            ParsekLog.Info("GameStateRecorder",
                $"Game state: strategy currency conversion - " +
                $"{StrategyConversionCapture.FormatQuery(snapshot, legs.Count)}");

            LedgerOrchestrator.OnStrategyCurrencyConversion(
                Planetarium.GetUniversalTime(), legs, snapshot.Reason);
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
            // re-capture subjects during replay - they are already committed.
            if (SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited("GameStateRecorder", "suppress-science-subject",
                    "Suppressed OnScienceReceived during timeline replay", 5.0);
                return;
            }

            if (subject == null || string.IsNullOrEmpty(subject.id))
            {
                ParsekLog.Verbose("GameStateRecorder", "OnScienceReceived: skipped - null subject or empty id");
                return;
            }

            CaptureScienceSubject(
                amount,
                subject.id,
                subject.science,
                subject.scienceCap,
                ReadScienceGainMultiplier(),
                Planetarium.GetUniversalTime(),
                vessel != null ? vessel.vesselName : null,
                // KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: pass the live launch
                // guid alongside the name so a recovery-science subject cannot be scoped to a
                // DIFFERENT launch of the same craft. Null (no ProtoVessel, or no vessel guid)
                // leaves the filter inert.
                VesselLaunchIdentity.ReadLaunchGuid(vessel));
        }

        /// <summary>
        /// Reads stock's career science-reward multiplier. <c>SubmitScienceData</c> adds the
        /// UNSCALED value to <c>subject.science</c> and fires <c>OnScienceRecieved</c> with the
        /// value multiplied by this, so dividing the event amount by it recovers the subject-unit
        /// increment. 1 when no game is loaded.
        /// </summary>
        private static float ReadScienceGainMultiplier()
        {
            var career = HighLogic.CurrentGame?.Parameters?.Career;
            return career != null ? career.ScienceGainMultiplier : 1f;
        }

        /// <summary>
        /// SCIENCE-SUBJECT-RUNNING-TOTAL-OVER-CREDIT: the per-callback science INCREMENT, in
        /// subject units (the units of <c>subject.science</c> / <c>scienceCap</c>, which the
        /// ledger's per-subject cap walk uses). Stock does <c>subject.science += value</c> and
        /// then fires the event with <c>value * ScienceGainMultiplier</c>, so the increment is
        /// <paramref name="amount"/> / multiplier. A ledger <c>ScienceEarning</c> row is ADDED
        /// by <see cref="ScienceModule.ProcessEarning"/>, so it must carry this increment, never
        /// the subject's running total (which re-credits every earlier submission of the same
        /// subject). Clamped to the running total, which the increment can never exceed.
        /// Pure.
        /// </summary>
        internal static float ComputeScienceSubjectIncrement(
            float amount,
            float scienceGainMultiplier,
            float subjectScienceAfter)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f)
                return 0f;

            float increment = amount;
            if (scienceGainMultiplier > 0f
                && !float.IsNaN(scienceGainMultiplier)
                && !float.IsInfinity(scienceGainMultiplier))
            {
                increment = amount / scienceGainMultiplier;
            }

            if (subjectScienceAfter > 0f
                && !float.IsInfinity(subjectScienceAfter)
                && increment > subjectScienceAfter)
            {
                increment = subjectScienceAfter;
            }

            return increment;
        }

        /// <summary>Test seam: installs the ScienceChanged capture a stock reward burst would have left.</summary>
        internal void SetLatestScienceChangeCaptureForTesting(RecentScienceChangeCapture capture)
        {
            latestScienceChangeCapture = capture;
        }

        /// <summary>
        /// Headless core of <see cref="OnScienceReceived"/>: every value the stock callback
        /// supplies is passed in, so the capture -> ledger chain is testable without KSP.
        /// </summary>
        internal void CaptureScienceSubject(
            float amount,
            string subjectId,
            float subjectScienceAfter,
            float subjectCap,
            float scienceGainMultiplier,
            double captureUt,
            string vesselName,
            string launchGuid)
        {
            if (string.IsNullOrEmpty(subjectId))
            {
                ParsekLog.Verbose("GameStateRecorder", "OnScienceReceived: skipped - null subject or empty id");
                return;
            }
            if (amount <= 0)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"OnScienceReceived: skipped - non-positive amount ({amount:F1}) for {subjectId}");
                return;
            }

            string currentRecordingId = ResolveCurrentRecordingTag() ?? "";
            string reasonKey = "";
            string subjectRecordingId = currentRecordingId;
            bool matchedScienceChange = false;
            RecentScienceChangeCapture matchedCapture = default(RecentScienceChangeCapture);
            if (ShouldUseRecentScienceChangeCapture(
                    latestScienceChangeCapture,
                    amount,
                    captureUt,
                    currentRecordingId))
            {
                matchedScienceChange = true;
                matchedCapture = latestScienceChangeCapture;
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

            // Operator ruling 2026-09-26: Breaking Ground deployed-experiment science is
            // earned by a ground station, not by whichever vessel is being flown, so it is
            // ALWAYS an untagged ledger row (like KSC / Tracking Station science). Never
            // tagged means a Re-Fly can neither tombstone it nor auto-seal a slot on it.
            bool deployedScience = IsDeployedScienceSubjectId(subjectId);
            if (deployedScience)
            {
                if (!string.IsNullOrEmpty(subjectRecordingId))
                {
                    ParsekLog.Verbose("GameStateRecorder",
                        $"OnScienceReceived: deployed-experiment subject '{subjectId}' routed untagged " +
                        $"(live tag '{subjectRecordingId}' ignored per the deployed-science ruling)");
                }
                subjectRecordingId = "";
                if (matchedScienceChange && !string.IsNullOrEmpty(matchedCapture.RecordingId))
                    UntagDeployedScienceChangeEvent(matchedCapture, subjectId);
            }

            // SCIENCE-SUBJECT-RUNNING-TOTAL-OVER-CREDIT: store the increment this callback
            // added, not subject.science. The ledger ADDS every ScienceEarning row per
            // subject (ScienceModule.ProcessEarning, BuildCommittedScienceSubjectCredits,
            // LedgerLoadMigration.GetLedgerScienceEarningTotal), so a running total would
            // re-credit every earlier submission of the same subject. subject.science may
            // include Harmony-injected committed science (ScienceSubjectPatch); the
            // increment is unaffected by that.
            float increment = ComputeScienceSubjectIncrement(
                amount, scienceGainMultiplier, subjectScienceAfter);
            var pendingSubject = new PendingScienceSubject
            {
                subjectId = subjectId,
                science = increment,
                subjectMaxValue = subjectCap,
                captureUT = captureUt,
                reasonKey = reasonKey,
                recordingId = subjectRecordingId
            };

            bool hasLiveRecorder = HasLiveRecorder();
            bool hasActiveUncommittedTree = HasActiveUncommittedTree();
            bool directLedgerHandled = false;
            if (deployedScience || ShouldForwardDirectScienceSubject(
                    pendingSubject.recordingId,
                    hasLiveRecorder,
                    hasActiveUncommittedTree))
            {
                directLedgerHandled = LedgerOrchestrator.TryRecordKscScienceSubject(
                    pendingSubject,
                    deployedScience ? null : vesselName,
                    deployedScience ? null : launchGuid);
            }

            if (!directLedgerHandled)
            {
                if (deployedScience)
                {
                    // Never park deployed science in PendingScienceSubjects: the commit-time
                    // window routing would hand an untagged subject to the committing
                    // recording, which is exactly the tagging the ruling forbids.
                    ParsekLog.Warn("GameStateRecorder",
                        $"OnScienceReceived: deployed-experiment subject '{subjectId}' was not " +
                        $"written to the ledger (increment={increment.ToString("R", CultureInfo.InvariantCulture)}) " +
                        "and is dropped rather than tagged to a recording");
                }
                else
                {
                    if (ShouldForwardDirectLedgerEvent(pendingSubject.recordingId, hasLiveRecorder) &&
                        hasActiveUncommittedTree)
                    {
                        ParsekLog.Verbose("GameStateRecorder",
                            $"OnScienceReceived: retained unowned science subject '{subjectId}' " +
                            "because an uncommitted recording tree is active");
                    }
                    PendingScienceSubjects.Add(pendingSubject);
                }
            }

            ParsekLog.Info("GameStateRecorder",
                $"Science subject captured: {subjectId} amount={amount:F1} " +
                $"increment={increment:F2} total={subjectScienceAfter:F1} " +
                $"reason='{reasonKey}' ut={captureUt:F1} tag='{subjectRecordingId}' " +
                $"deployed={deployedScience} directLedger={directLedgerHandled}");
        }

        /// <summary>
        /// A deployed-science award's ScienceChanged event was emitted (tagged to the live
        /// recording) one callback BEFORE the subject was known. Clear that tag so the
        /// commit-time earnings-window reconcile does not count a delta the recording never
        /// owns. Identity is the matched capture's (ut, key, tag).
        /// </summary>
        private static void UntagDeployedScienceChangeEvent(
            RecentScienceChangeCapture capture,
            string subjectId)
        {
            var target = new GameStateEvent
            {
                ut = capture.Ut,
                eventType = GameStateEventType.ScienceChanged,
                key = capture.ReasonKey ?? "",
                recordingId = capture.RecordingId ?? ""
            };
            bool updated = GameStateStore.UpdateEventRecordingId(target, "");
            ParsekLog.Verbose("GameStateRecorder",
                $"OnScienceReceived: deployed-experiment ScienceChanged event for '{subjectId}' " +
                $"ut={capture.Ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"untagged={updated} (was '{capture.RecordingId}')");
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

        /// <summary>
        /// GameEvents.OnKSCFacilityUpgrading handler: records an in-scene facility upgrade
        /// (UI or seam) immediately, without waiting for the next scene-load poll.
        /// </summary>
        private void OnFacilityUpgrading(Upgradeables.UpgradeableFacility fac, int newLevelIndex)
        {
            facilityRecorder.OnFacilityUpgrading(fac, newLevelIndex);
        }

        /// <summary>GameEvents.OnKSCStructureCollapsing handler (see GameStateFacilityRecorder).</summary>
        private void OnStructureCollapsing(DestructibleBuilding db)
        {
            facilityRecorder.OnStructureCollapsing(db);
        }

        /// <summary>GameEvents.OnKSCStructureRepairing handler (see GameStateFacilityRecorder).</summary>
        private void OnStructureRepairing(DestructibleBuilding db)
        {
            facilityRecorder.OnStructureRepairing(db);
        }

        /// <summary>FacilityRepairCapture.StructuresReset handler (see GameStateFacilityRecorder).</summary>
        private void OnStructuresReset(IList<string> buildingIds)
        {
            facilityRecorder.OnStructuresReset(buildingIds);
        }

        /// <summary>
        /// Test seam: records a building transition through the same path the stock events
        /// use, with an explicit UT (the handlers read Planetarium, which is absent headless).
        /// </summary>
        internal bool RecordBuildingTransitionForTesting(
            string buildingId, bool nowIntact, double ut, float repairCost, string source)
        {
            return facilityRecorder.RecordBuildingTransition(buildingId, nowIntact, ut, repairCost, source);
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
