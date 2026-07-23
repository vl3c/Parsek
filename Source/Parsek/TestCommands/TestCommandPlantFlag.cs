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
        /// <paramref name="gateOpen"/> samples <c>Events["PlantFlag"].active</c> every poll.
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
