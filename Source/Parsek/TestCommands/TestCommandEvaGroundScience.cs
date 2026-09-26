using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The two stock player actions <c>EvaGroundScience</c> drives.</summary>
    internal enum EvaGroundScienceAction
    {
        /// <summary>Place a ground-deployable part from the EVA kerbal's inventory.</summary>
        Place,

        /// <summary>Pick a deployed ground part back up into the kerbal's inventory.</summary>
        Pickup,
    }

    /// <summary>Per-poll decision while the placement preview is live.</summary>
    internal enum GroundPlaceConfirmDecision
    {
        /// <summary>The preview is still being built, or a press is in flight: poll again.</summary>
        Wait,

        /// <summary>The preview is built and stock reads the spot as placeable: press the
        /// EVA jump key (stock's placement-confirm binding) on the next frame.</summary>
        Press,

        /// <summary>Every allowed press was spent and the preview is still up.</summary>
        GiveUp,
    }

    /// <summary>The two-phase completion outcome, shared by both actions.</summary>
    internal enum GroundScienceCompletionDecision
    {
        StillWaiting,
        CompleteOk,
        Timeout,
    }

    /// <summary>
    /// Pure decision core for the <c>EvaGroundScience</c> seam verb (coverage wave 10, the
    /// D7 <c>inventory-place-remove</c> cell). The Unity applier is
    /// <c>ParsekTestCommandAddon.EvaGroundScience.cs</c>.
    ///
    /// <para>WHAT IT DRIVES, decompiled from KSP 1.12.5 Assembly-CSharp. PLACE is the
    /// inventory PAW slot click (<c>UIPartActionInventory.StartPartPlacement</c> calls
    /// the public <c>ModuleInventoryPart.DeployInventoryItem(slot)</c>, which builds the
    /// placement preview) followed by the confirm key: <c>ModuleInventoryPart.OnUpdate</c>
    /// places the part only when <c>GameSettings.EVA_Jump.GetKeyDown()</c> reads true with
    /// the preview on terrain, inside the ground-offset cap and collision-free. The
    /// applier presses that key for exactly one frame through a Harmony prefix on
    /// <c>KeyBinding.GetKeyDown</c> scoped to the <c>EVA_Jump</c> instance (installed
    /// for the press and removed straight after), which still honours the binding's input
    /// lock. Everything after the key - <c>DeployGroundPart</c> -> <c>AddVessel</c> -> the
    /// new vessel's <c>ModuleGroundSciencePart.OnStart</c> firing
    /// <c>GameEvents.onGroundSciencePartDeployed</c> - is stock's own code; the seam fires
    /// no GameEvent. PICKUP is the part's PAW "Pick Up" button, the
    /// <c>ModuleGroundPart.RetrievePart</c> KSPEvent, invoked through its own
    /// <c>BaseEvent</c>; stock fires <c>onGroundSciencePartRemoved</c> from inside it.</para>
    /// </summary>
    internal static class TestCommandEvaGroundScience
    {
        internal const string ActionArg = "action";
        internal const string PartArg = "part";
        internal const string PlaceToken = "place";
        internal const string PickupToken = "pickup";

        /// <summary>Optional <c>faceAway=true</c> on place: turn the kerbal away from the
        /// nearest other loaded vessel first (a kerbal just off a ladder faces the hull, and
        /// stock puts the preview straight ahead of it).</summary>
        internal const string FaceAwayArg = "faceAway";

        /// <summary>Presses allowed before the confirm phase gives up. Stock answers an
        /// invalid spot with its "not allowed" sound and leaves the preview up, so one
        /// press is not always the last word; five is generous for a kerbal standing
        /// still on flat ground.</summary>
        internal const int MaxConfirmPresses = 5;

        /// <summary>Frames to wait after a press before reading its effect (the press
        /// frame itself, then the frame stock's placement runs in).</summary>
        internal const int FramesBetweenPresses = 10;

        /// <summary>Frames the new or removed vessel must stay in its final state before
        /// OK, so the recorder's own line lands before the next step.</summary>
        internal const int SettleFrames = 30;

        internal static bool TryParseAction(string raw, out EvaGroundScienceAction action)
        {
            action = EvaGroundScienceAction.Place;
            if (raw == PlaceToken) return true;
            if (raw == PickupToken)
            {
                action = EvaGroundScienceAction.Pickup;
                return true;
            }
            return false;
        }

        /// <summary>KSP's runtime part names use dots where the cfg used underscores.</summary>
        internal static string NormalizePartName(string raw)
            => string.IsNullOrEmpty(raw) ? raw : raw.Replace('_', '.');

        /// <summary>The lowest slot index whose stored part is <paramref name="partName"/>
        /// (ordinal match), or -1.</summary>
        internal static int FindSlotHolding(IEnumerable<KeyValuePair<int, string>> slots,
            string partName)
        {
            int best = -1;
            if (slots == null || string.IsNullOrEmpty(partName)) return best;
            foreach (var kv in slots)
            {
                if (kv.Value != partName) continue;
                if (best < 0 || kv.Key < best) best = kv.Key;
            }
            return best;
        }

        /// <summary>The injected key press is live on exactly the armed frame.</summary>
        internal static bool IsInjectedPressFrame(int armedFrame, int currentFrame)
            => armedFrame >= 0 && currentFrame == armedFrame;

        /// <summary>
        /// One poll of the confirm phase. <paramref name="previewUp"/> is stock's
        /// <c>selectedPart != null</c>; <paramref name="previewBuilt"/> its
        /// <c>partFullyCreated</c>; <paramref name="spotPlaceable"/> the conjunction the
        /// confirm branch itself tests (on terrain, inside the cap, no collisions).
        /// </summary>
        /// <summary>Frames an unplaceable preview is given before the face-away turn is
        /// applied again.</summary>
        internal const int ReTurnFrames = 60;

        /// <summary>Upper bound on the face-away re-turns of one placement.</summary>
        internal const int MaxReTurns = 5;

        /// <summary>
        /// Should the face-away turn be applied again? A kerbal released from the ladder
        /// can still be settling when the first turn is applied, and stock re-reads
        /// <c>vesselTransform.forward</c> every frame for the preview spot, so a spot that
        /// stays unplaceable (the terrain ray lands on the hull) is retried with a fresh
        /// turn, a bounded number of times, and never once a confirm press was sent.
        /// </summary>
        internal static bool ShouldReTurn(
            bool faceAway, bool previewBuilt, bool spotPlaceable, int pressesSoFar,
            int framesSinceTurn, int reTurnsSoFar)
        {
            if (!faceAway || !previewBuilt || spotPlaceable) return false;
            if (pressesSoFar > 0) return false;
            if (reTurnsSoFar >= MaxReTurns) return false;
            return framesSinceTurn >= ReTurnFrames;
        }

        internal static GroundPlaceConfirmDecision DecideConfirm(
            bool previewUp, bool previewBuilt, bool spotPlaceable,
            int pressesSoFar, int framesSinceLastPress)
        {
            if (!previewUp) return GroundPlaceConfirmDecision.Wait;
            if (pressesSoFar > 0 && framesSinceLastPress < FramesBetweenPresses)
                return GroundPlaceConfirmDecision.Wait;
            if (pressesSoFar >= MaxConfirmPresses) return GroundPlaceConfirmDecision.GiveUp;
            if (!previewBuilt || !spotPlaceable) return GroundPlaceConfirmDecision.Wait;
            return GroundPlaceConfirmDecision.Press;
        }

        /// <summary>Place completes once the preview is gone, the slot is empty and the new
        /// ground vessel is loaded and has held that state for the settle window.</summary>
        internal static GroundScienceCompletionDecision DecidePlaceCompletion(
            double elapsed, double budget, bool previewGone, bool slotCleared,
            bool newVesselLoaded, int settledFrames)
        {
            if (previewGone && slotCleared && newVesselLoaded && settledFrames >= SettleFrames)
                return GroundScienceCompletionDecision.CompleteOk;
            return elapsed >= budget
                ? GroundScienceCompletionDecision.Timeout
                : GroundScienceCompletionDecision.StillWaiting;
        }

        /// <summary>Pickup completes once the ground vessel is gone and the kerbal's
        /// inventory holds the part again, held for the settle window.</summary>
        internal static GroundScienceCompletionDecision DecidePickupCompletion(
            double elapsed, double budget, bool vesselGone, bool inventoryHoldsPart,
            int settledFrames)
        {
            if (vesselGone && inventoryHoldsPart && settledFrames >= SettleFrames)
                return GroundScienceCompletionDecision.CompleteOk;
            return elapsed >= budget
                ? GroundScienceCompletionDecision.Timeout
                : GroundScienceCompletionDecision.StillWaiting;
        }

        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            EvaGroundScienceAction action, string partName, uint partPid, uint vesselPid,
            int slot, int presses, double distanceMeters)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("action",
                    action == EvaGroundScienceAction.Place ? PlaceToken : PickupToken),
                new KeyValuePair<string, string>("part", partName ?? string.Empty),
                new KeyValuePair<string, string>("partPid",
                    partPid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("vesselPid",
                    vesselPid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("slot",
                    slot.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("presses",
                    presses.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("distance",
                    distanceMeters.ToString("F2", CultureInfo.InvariantCulture)),
            };
    }
}
