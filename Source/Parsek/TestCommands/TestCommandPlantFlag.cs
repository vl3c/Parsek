using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The bounded-wait pre-plant gate outcome (M-C2, F1). The stock plant gate
    /// (<c>Events["PlantFlag"].active</c>) is TRANSIENTLY false while the kerbal lands
    /// after an <c>EvaExit release=true</c>, so a single one-shot read would terminally
    /// REJECT a plant that succeeds two seconds later (the EVA-1 near-deterministic
    /// failure). The verb therefore treats the gate as a WAIT, not a precondition snapshot.
    /// </summary>
    internal enum PlantGateDecision
    {
        /// <summary>The gate is transiently closed (mid-fall / stumble / ragdoll recovery):
        /// keep polling. NEVER a terminal reject.</summary>
        KeepWaiting,

        /// <summary>The gate is open: fire <c>PlantFlag()</c> exactly once on this
        /// transition, then move to the dialog phase.</summary>
        ProceedToPlant,

        /// <summary>The AC flag unlock is closed (a stably-closed cause that cannot flip
        /// mid-mission): terminal REJECTED (msg=flag-lock-stable).</summary>
        RejectStableLock,

        /// <summary>The budget expired with the gate never open: terminal ERROR
        /// (msg=flag-gate-timeout).</summary>
        GateTimeout,
    }

    /// <summary>The two-phase flag-plant completion outcome (after the gate opened and
    /// <c>PlantFlag()</c> ran).</summary>
    internal enum FlagPlantCompletionDecision
    {
        /// <summary>The plant FSM animation / dialog has not completed yet: keep polling.</summary>
        StillWaiting,

        /// <summary>The FlagSite vessel exists, the SiteRename dialog was answered, and the
        /// scene settled: terminal OK.</summary>
        CompleteOk,

        /// <summary>The budget expired after the plant fired (animation never completed or
        /// dialog never spawned): terminal ERROR (msg=flag-timeout). The decremented flag
        /// item is lost, documented, not recovered.</summary>
        FlagTimeout,
    }

    /// <summary>Phase-B SiteRename dialog action, decided per poll after <c>PlantFlag()</c>
    /// fired.</summary>
    internal enum SiteRenameDialogAction
    {
        /// <summary>The popup is not live yet (the plant animation is still running): keep
        /// polling. The dialog is answered ONLY through the real button callback once the
        /// popup actually spawns.</summary>
        KeepWaiting,

        /// <summary>The live "SiteRename" popup was found: invoke its dismiss button's own
        /// callback so <c>GameEvents.afterFlagPlanted.Fire</c> runs synchronously.</summary>
        InvokeDismiss,
    }

    /// <summary>
    /// Pure decision helpers for the two-phase, bounded-wait-gated, dialog-answering
    /// <c>PlantFlag</c> seam verb (M-C2, design-autotest-eva-missions.md). The Unity
    /// applier polls <see cref="DecidePlantGateWait"/> until the stock gate opens, calls
    /// <c>KerbalEVA.PlantFlag()</c> on the open transition, answers the "SiteRename" popup
    /// via its own button callback, then polls <see cref="DecideFlagPlantCompletion"/>.
    /// Kept pure so the mid-fall bounded-wait regression + the false-OK-over-unanswered-
    /// dialog guard are xUnit-covered without a live FSM.
    /// </summary>
    internal static class TestCommandPlantFlag
    {
        /// <summary>
        /// Decide the bounded-wait pre-plant phase (F1). Positive-first: an open gate
        /// PROCEEDS (the plant fires exactly once on that transition); a stably-closed AC
        /// flag unlock is the terminal reject; the budget expiry with a never-open gate is
        /// the terminal timeout; a transiently-closed gate KEEPS WAITING (never a terminal
        /// reject - the mid-fall regression). <paramref name="stableLockClosed"/> is the AC
        /// flag unlock read directly (the one cause that cannot flip mid-mission);
        /// <paramref name="gateOpen"/> is the LIVE gate from <see cref="IsPlantGateOpen"/>
        /// (a per-poll <c>CanPlantFlag()</c> read plus the plantable-fsm-state check), NOT the
        /// stale edge-triggered <c>Events["PlantFlag"].active</c> cache that stranded EVA-1.
        /// </summary>
        internal static PlantGateDecision DecidePlantGateWait(
            double elapsed, bool gateOpen, bool stableLockClosed, double budget)
        {
            if (gateOpen)
                return PlantGateDecision.ProceedToPlant;
            if (stableLockClosed)
                return PlantGateDecision.RejectStableLock;
            if (elapsed >= budget)
                return PlantGateDecision.GateTimeout;
            return PlantGateDecision.KeepWaiting;
        }

        /// <summary>
        /// The LIVE plant-gate-open decision (EVA-1 pad-flag defect, 2026-07-24). The stock
        /// button flag <c>Events["PlantFlag"].active</c> is NOT the availability truth: it is
        /// an edge-triggered CACHE (decompiled <c>KerbalEVA</c>, KSP 1.12.5) assigned
        /// <c>= CanPlantFlag()</c> only at <c>idle_OnEnter</c> / <c>idle_b_OnEnter</c>
        /// (entering <c>st_idle_gr</c> / <c>st_idle_b_gr</c>), a vessel-situation change WHILE
        /// already in <c>st_idle_gr</c>, a construction-mode toggle, go-off-rails, or
        /// <c>AddFlag</c>. A kerbal that lands and stands still on the pad after an
        /// <c>EvaExit release=true</c> enters <c>st_idle_gr</c> exactly ONCE - and if the
        /// cache is computed while ground contact / ragdoll-settle is still transient it
        /// latches stale-false, then NOTHING re-fires, so the button never opens even though
        /// <c>CanPlantFlag()</c> flips true a frame later. That is the 180s EVA-1 timeout. The
        /// seam therefore reads the live <paramref name="canPlantFlag"/> (a direct
        /// <c>CanPlantFlag()</c> call, recomputed every poll) AND confirms the kerbal is in a
        /// state where <c>PlantFlag()</c>'s <c>On_flagPlantStart</c> (a MANUAL_TRIGGER event
        /// registered ONLY on <c>st_idle_gr</c> / <c>st_idle_b_gr</c>) will actually fire
        /// (<paramref name="inPlantableFsmState"/>); otherwise <c>PlantFlag()</c> would
        /// decrement <c>flagItems</c> without planting.
        /// </summary>
        internal static bool IsPlantGateOpen(bool canPlantFlag, bool inPlantableFsmState)
            => canPlantFlag && inPlantableFsmState;

        /// <summary>
        /// Build a compact, self-explaining unmet-precondition token for the gate-wait /
        /// timeout diagnostic (liveness rule: a timeout must name WHICH stock precondition is
        /// closed, not just <c>gateOpen=false</c>). The booleans mirror the decompiled
        /// <c>CanPlantFlag()</c> conjuncts plus the plantable-fsm-state gate. Returns
        /// <c>"open"</c> when every precondition is met; otherwise a comma-joined list of the
        /// closed ones (e.g. <c>"fsm=Idle_Grounded,no-ground-contact"</c>). Never returns an
        /// empty string.
        /// </summary>
        internal static string DescribePlantGateBlock(
            bool inPlantableFsmState, bool vesselActive, bool groundContact,
            bool flagItemsPositive, bool notRagdoll, bool flagUnlocked, bool notConstruction,
            string fsmStateName)
        {
            var unmet = new List<string>();
            if (!inPlantableFsmState) unmet.Add("fsm=" + (string.IsNullOrEmpty(fsmStateName) ? "?" : fsmStateName));
            if (!vesselActive) unmet.Add("vessel-not-active");
            if (!groundContact) unmet.Add("no-ground-contact");
            if (!flagItemsPositive) unmet.Add("no-flag-items");
            if (!notRagdoll) unmet.Add("ragdoll");
            if (!flagUnlocked) unmet.Add("ac-flag-locked");
            if (!notConstruction) unmet.Add("construction-mode");
            return unmet.Count == 0 ? "open" : string.Join(",", unmet);
        }

        /// <summary>
        /// Decide the flag-plant completion. CompleteOk requires the FlagSite vessel to
        /// exist, the dialog to have been answered, and the scene to have settled. The
        /// dialogAnswered flag is set by the applier WHEN it invokes the button - never
        /// inferred from popup absence (a never-spawned popup would false-OK otherwise).
        /// Positive completion first, then budget, StillWaiting as the default.
        /// </summary>
        internal static FlagPlantCompletionDecision DecideFlagPlantCompletion(
            double elapsed, bool flagSiteVesselExists, bool dialogAnswered,
            bool sceneSettled, double budget)
        {
            if (flagSiteVesselExists && dialogAnswered && sceneSettled)
                return FlagPlantCompletionDecision.CompleteOk;
            if (elapsed >= budget)
                return FlagPlantCompletionDecision.FlagTimeout;
            return FlagPlantCompletionDecision.StillWaiting;
        }

        /// <summary>
        /// Decide the Phase-B SiteRename dialog action. Decompile-proven (FlagSite.cs, KSP
        /// 1.12.5): the FlagSite vessel is created at <c>KerbalEVA.flagPlant_OnEnter</c> via
        /// <c>FlagSite.CreateFlag</c> - BEFORE the plant animation completes and long before
        /// <c>FlagSite.OnPlacementComplete -> RenameSite</c> spawns the "SiteRename" popup, and
        /// <c>GameEvents.afterFlagPlanted.Fire</c> runs ONLY inside that dialog's afterDialog
        /// button callback. So the presence of the FlagSite vessel is NOT evidence the dialog
        /// was answered: we must WAIT for the popup to actually spawn, then invoke its button.
        /// Never infer "answered" from popup-absence + flag-vessel-presence (that false-OK'd the
        /// plant before the dialog spawned and afterFlagPlanted never fired - EVA-1 flight 3,
        /// 2026-07-24). <paramref name="flagVesselExists"/> is deliberately non-decisive; it is
        /// accepted only to make the "flag vessel exists yet we still wait" contract explicit.
        /// </summary>
        internal static SiteRenameDialogAction DecideSiteRenameDialogAction(
            bool popupPresent, bool flagVesselExists)
            => popupPresent ? SiteRenameDialogAction.InvokeDismiss : SiteRenameDialogAction.KeepWaiting;

        /// <summary>Terminal completion payload once the flag vessel exists + the dialog answered.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string flagSite, string body, double lat, double lon)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flagSite", flagSite ?? string.Empty),
                new KeyValuePair<string, string>("body", body ?? string.Empty),
                new KeyValuePair<string, string>("lat",
                    lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("lon",
                    lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            };
    }
}
