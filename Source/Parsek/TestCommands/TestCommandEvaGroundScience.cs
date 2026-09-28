using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The three stock player actions <c>EvaGroundScience</c> drives.</summary>
    internal enum EvaGroundScienceAction
    {
        /// <summary>Place a ground-deployable part from the EVA kerbal's inventory.</summary>
        Place,

        /// <summary>Pick a deployed ground part back up into the kerbal's inventory.</summary>
        Pickup,

        /// <summary>Move a stored part from a nearby vessel's inventory container into the
        /// EVA kerbal's inventory: the inventory window drag a player makes to carry the
        /// next part of a multi-part cluster (one Breaking Ground part fills most of a
        /// kerbal's 40 L).</summary>
        Take,
    }

    /// <summary>One stored part a take could come from: a container slot on a loaded
    /// vessel, with the kerbal's distance to that container's part.</summary>
    internal struct GroundTakeCandidate
    {
        public int ContainerIndex;
        public int Slot;
        public string PartName;
        public double DistanceMeters;
    }

    /// <summary>The pure take-source verdict.</summary>
    internal enum GroundTakeSourceDecision
    {
        /// <summary><see cref="GroundTakeSourceChoice.Candidate"/> names the source.</summary>
        Found,

        /// <summary>No loaded container stores the part.</summary>
        NotStored,

        /// <summary>A container stores it, but none within stock's EVA inventory reach.</summary>
        OutOfRange,
    }

    internal struct GroundTakeSourceChoice
    {
        public GroundTakeSourceDecision Decision;
        public GroundTakeCandidate Candidate;

        /// <summary>The nearest container holding the part, in range or not (for the
        /// out-of-range refusal line); <see cref="double.MaxValue"/> when none.</summary>
        public double NearestDistanceMeters;
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
        internal const string TakeToken = "take";

        /// <summary>Optional <c>faceAway=true</c> on place: turn the kerbal away from the
        /// nearest other loaded vessel first (a kerbal just off a ladder faces the hull, and
        /// stock puts the preview straight ahead of it).</summary>
        internal const string FaceAwayArg = "faceAway";

        /// <summary>Presses allowed before the confirm phase gives up. Stock answers an
        /// invalid spot with its "not allowed" sound and leaves the preview up, so one
        /// press is not always the last word. Sized to leave one press per face-away
        /// re-turn (<see cref="MaxReTurns"/>).</summary>
        internal const int MaxConfirmPresses = 8;

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
            if (raw == TakeToken)
            {
                action = EvaGroundScienceAction.Take;
                return true;
            }
            return false;
        }

        /// <summary>The wire token for an action (the inverse of <see cref="TryParseAction"/>).</summary>
        internal static string ActionToken(EvaGroundScienceAction action)
        {
            switch (action)
            {
                case EvaGroundScienceAction.Pickup: return PickupToken;
                case EvaGroundScienceAction.Take: return TakeToken;
                default: return PlaceToken;
            }
        }

        /// <summary>
        /// Chooses the container slot a take moves the part out of: among candidates
        /// storing <paramref name="partName"/> (ordinal) within
        /// <paramref name="reachMeters"/> (stock's <c>GameSettings.EVA_INVENTORY_RANGE</c>,
        /// the distance at which a kerbal can open a container's inventory), the NEAREST
        /// container, and inside it the LOWEST slot. Ties on distance keep the lower
        /// container index, so the choice is deterministic.
        /// </summary>
        internal static GroundTakeSourceChoice ChooseTakeSource(
            IEnumerable<GroundTakeCandidate> candidates, string partName, double reachMeters)
        {
            var choice = new GroundTakeSourceChoice
            {
                Decision = GroundTakeSourceDecision.NotStored,
                NearestDistanceMeters = double.MaxValue,
            };
            if (candidates == null || string.IsNullOrEmpty(partName)) return choice;
            bool found = false;
            GroundTakeCandidate best = default(GroundTakeCandidate);
            foreach (GroundTakeCandidate c in candidates)
            {
                if (c.PartName != partName) continue;
                if (c.DistanceMeters < choice.NearestDistanceMeters)
                    choice.NearestDistanceMeters = c.DistanceMeters;
                if (c.DistanceMeters > reachMeters) continue;
                bool better = !found
                    || c.DistanceMeters < best.DistanceMeters
                    || (c.DistanceMeters == best.DistanceMeters
                        && (c.ContainerIndex < best.ContainerIndex
                            || (c.ContainerIndex == best.ContainerIndex && c.Slot < best.Slot)));
                if (better)
                {
                    best = c;
                    found = true;
                }
            }
            if (found)
            {
                choice.Decision = GroundTakeSourceDecision.Found;
                choice.Candidate = best;
            }
            else if (choice.NearestDistanceMeters < double.MaxValue)
            {
                choice.Decision = GroundTakeSourceDecision.OutOfRange;
            }
            return choice;
        }

        /// <summary>A take completes once the kerbal's inventory holds the part AND the
        /// source slot no longer does, held for the settle window.</summary>
        internal static GroundScienceCompletionDecision DecideTakeCompletion(
            double elapsed, double budget, bool kerbalHoldsPart, bool sourceSlotCleared,
            int settledFrames)
        {
            if (kerbalHoldsPart && sourceSlotCleared && settledFrames >= SettleFrames)
                return GroundScienceCompletionDecision.CompleteOk;
            return elapsed >= budget
                ? GroundScienceCompletionDecision.Timeout
                : GroundScienceCompletionDecision.StillWaiting;
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

        /// <summary>Frames an unplaceable preview is given before the face-away turn is
        /// applied again, counted from the last turn and, once a press went out, also from
        /// that press (the press's own settle window).</summary>
        internal const int ReTurnFrames = 60;

        /// <summary>Upper bound on the face-away re-turns of one placement.</summary>
        internal const int MaxReTurns = 8;

        /// <summary>Heading drift, in degrees, past which the seam re-asserts the chosen
        /// face-away heading. A kerbal turned with <c>Vessel.SetRotation</c> drifts back
        /// to face the hull within about a second (EVA-5/6/7 logs: re-turn angles of
        /// 146-166 degrees one second after a turn to 0).</summary>
        internal const double HeadingHoldToleranceDegrees = 10.0;

        /// <summary>Upper bound on heading re-assertions of one placement (about 20 s of
        /// frames); past it the bounded re-turns are the only correction left.</summary>
        internal const int MaxHeadingHolds = 1200;

        /// <summary>Offsets, in degrees about the local up axis, tried on successive
        /// face-away turns: straight away first, then fanned out, so a spot blocked by
        /// something other than the nearest vessel is not retried on the same line.</summary>
        private static readonly double[] TurnOffsetLadderDegrees = { 0.0, 30.0, -30.0, 60.0, -60.0 };

        /// <summary>The heading offset for face-away turn <paramref name="turnIndex"/> (0 is
        /// the first turn, n the n-th re-turn); the ladder repeats.</summary>
        internal static double TurnOffsetDegrees(int turnIndex)
        {
            if (turnIndex <= 0) return 0.0;
            return TurnOffsetLadderDegrees[turnIndex % TurnOffsetLadderDegrees.Length];
        }

        /// <summary>
        /// Should the face-away turn be applied again? The preview spot is re-read from
        /// <c>vesselTransform.forward</c> every frame, so a spot that stays unplaceable
        /// (the terrain ray lands on a hull collider) is retried with a fresh turn, a
        /// bounded number of times. A confirm press that did not land (the preview is
        /// still up past the press's settle window and the spot now reads blocked) does
        /// NOT end the retries: run <c>2026-09-27_1344</c> pressed once, the press missed,
        /// and the spot then stayed blocked for the whole 120 s budget.
        /// </summary>
        internal static bool ShouldReTurn(
            bool faceAway, bool previewBuilt, bool spotPlaceable, int pressesSoFar,
            int framesSinceTurn, int framesSinceLastPress, int reTurnsSoFar)
        {
            if (!faceAway || !previewBuilt || spotPlaceable) return false;
            if (reTurnsSoFar >= MaxReTurns) return false;
            if (pressesSoFar > 0 && framesSinceLastPress < ReTurnFrames) return false;
            return framesSinceTurn >= ReTurnFrames;
        }

        /// <summary>
        /// Should the seam re-assert the chosen face-away heading this poll? Only once a
        /// heading was chosen, only until the placement is confirmed, only past the drift
        /// tolerance, and at most <see cref="MaxHeadingHolds"/> times.
        /// </summary>
        internal static bool ShouldHoldHeading(
            bool faceAway, bool headingChosen, bool placementConfirmed,
            double driftDegrees, int holdsSoFar)
        {
            if (!faceAway || !headingChosen || placementConfirmed) return false;
            if (holdsSoFar >= MaxHeadingHolds) return false;
            return driftDegrees > HeadingHoldToleranceDegrees;
        }

        /// <summary>
        /// One poll of the confirm phase. <paramref name="previewUp"/> is stock's
        /// <c>selectedPart != null</c>; <paramref name="previewBuilt"/> its
        /// <c>partFullyCreated</c>; <paramref name="spotPlaceable"/> the conjunction the
        /// confirm branch itself tests (on terrain, inside the cap, no collisions).
        /// </summary>
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
                new KeyValuePair<string, string>("action", ActionToken(action)),
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
