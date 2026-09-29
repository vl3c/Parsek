using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens.Flight;
using KSP.UI.Screens.Flight.Dialogs;
using KSP.UI.TooltipTypes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>Which in-flight crew move a stock control would start.</summary>
    internal enum FlightCrewMove
    {
        Eva,
        Transfer
    }

    /// <summary>
    /// In-flight crew moves of a held kerbal (block audit row K2,
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md): a kerbal a committed
    /// flight holds, who is aboard a live vessel that is NOT the continuation of a committed
    /// flight, cannot be taken out on EVA or transferred to another part. Without this, the
    /// spawn-time crew dedup later empties his seat in the recorded flight
    /// (<c>VesselSpawner.RemoveDuplicateCrewFromSnapshot</c>).
    ///
    /// <para>ONE predicate, <see cref="IsMoveRefused"/>, reused from the crew dialog's
    /// (<see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>, which already carries the
    /// live Re-Fly crew carve-out) and narrowed twice:
    /// <list type="bullet">
    /// <item>only a kerbal a committed flight HOLDS now (<c>KerbalReservationKind.ReservedActive</c>,
    /// on-flight or lost). A retired stand-in is refused a new crew by the dialog, but every
    /// flight he flew is over, so moving him risks no committed flight and refusing would
    /// strand him aboard.</item>
    /// <item>never aboard a vessel that continues a committed flight: a Parsek-spawned or
    /// adopted vessel (<c>CrewReservationManager.ActiveVesselIsParsekSpawned</c>, the same
    /// exemption the flight-ready crew swap applies). Flying on from a committed flight's
    /// end is how a hold ends (recovery from a vessel continuing that flight).</item>
    /// <item>never while a Re-Fly session is active (design section 3.3.1: EVA and transfer
    /// are not blocked during a re-fly). The origin flight's rows stay effective until the
    /// merge, so the re-fly crew read as held; the crew dialog's carve-out
    /// (<c>CrewReservationManager.IsLiveReFlyCrew</c>) only recognises the provisional
    /// vessel by its pid, which a decoupled / undocked child or a dock that keeps the other
    /// pid does not share. The whole block stands down for the session instead of trying
    /// to scope the re-fly's live tree; the merge settles the holds.</item>
    /// </list></para>
    ///
    /// <para>Controls, stock mechanisms only (owner ruling D1): the portrait EVA button is
    /// greyed with the reason in its own stock tooltip (appended after stock's locked reason
    /// when stock also refuses); the crew hatch dialog greys EVA and Transfer for that row,
    /// adds the status to the row's own name label and the reason to a stock tooltip on each
    /// greyed button. Backstops, same predicate and text: prefixes on
    /// <c>FlightEVA.spawnEVA</c> and <c>CrewTransfer.Create</c>, the only stock entry
    /// points (whole-assembly IL scan, KSP 1.12.5).</para>
    /// </summary>
    internal static class StockUiFlightCrewDecoration
    {
        private const string Tag = "StockUiOverlay";
        private const string BlockTag = "CrewMove";

        /// <summary>The tab the pass log names for the crew hatch dialog.</summary>
        internal const string CrewHatchTab = "CrewHatch";

        /// <summary>The tab the pass log names for a portrait.</summary>
        internal const string PortraitTab = "Portrait";

        /// <summary>How often a hovered portrait re-derives its decision (seconds, real time).
        /// Stock rewrites the button every hovered frame; the decision itself changes only
        /// with the ledger, so a cached decision is re-applied in between.</summary>
        internal const float PortraitRecheckSeconds = 1f;

        // ---------------- pure decisions ----------------

        /// <summary>
        /// THE refusal predicate for an in-flight crew move. <paramref name="refusedByCrewDialog"/>
        /// is <see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>; <paramref name="kind"/> is
        /// <see cref="KerbalsModule.GetReservationKind"/>; <paramref name="vesselContinuesCommittedFlight"/>
        /// is true when the kerbal is aboard a Parsek-spawned or adopted vessel.
        /// </summary>
        internal static bool IsMoveRefused(
            bool refusedByCrewDialog, KerbalReservationKind kind, bool vesselContinuesCommittedFlight,
            bool reFlySessionActive = false)
        {
            return !reFlySessionActive
                && refusedByCrewDialog
                && kind == KerbalReservationKind.ReservedActive
                && !vesselContinuesCommittedFlight;
        }

        /// <summary>
        /// The decision for one kerbal aboard one vessel: blocked exactly when
        /// <see cref="IsMoveRefused"/> holds, with the crew dialog's own text for a held kerbal
        /// (the on-flight or lost explanation), so the EVA / Transfer reason reads the same as
        /// the VAB crew dialog and the Astronaut Complex.
        /// </summary>
        internal static StockUiDecoration Decide(
            string kerbalName,
            bool refusedByCrewDialog,
            KerbalReservationKind kind,
            bool vesselContinuesCommittedFlight,
            CommittedFutureIndex index,
            AstronautComplexContext context,
            Func<double, string> formatDate,
            string tab = CrewHatchTab,
            bool reFlySessionActive = false)
        {
            bool refused = IsMoveRefused(refusedByCrewDialog, kind, vesselContinuesCommittedFlight, reFlySessionActive);
            var d = StockUiCrewDialogDecoration.Decide(
                kerbalName, refused, refused ? kind : KerbalReservationKind.NotManaged,
                index, context, formatDate);
            d.Screen = StockUiScreen.FlightCrew;
            d.Tab = tab;
            return d;
        }

        /// <summary>The refused-click dialog title.</summary>
        internal static string BlockedTitle(FlightCrewMove move, string kerbalName)
        {
            return (move == FlightCrewMove.Eva ? "Cannot EVA \"" : "Cannot transfer \"")
                + (kerbalName ?? "") + "\"";
        }

        /// <summary>
        /// The portrait EVA tooltip: the reason alone when stock would allow the EVA, else
        /// stock's own locked reason with the reason appended on its own line (stock first).
        /// </summary>
        internal static string PortraitTooltip(bool stockAllowed, string stockText, string why)
        {
            if (stockAllowed || string.IsNullOrEmpty(stockText)) return why ?? "";
            return StockUiRnDDecoration.AppendReason(stockText, why, "portrait EVA tooltip");
        }

        /// <summary>The hatch dialog row label: stock's name plus the status, in the reason colour.</summary>
        internal static string HatchRowLabel(string kerbalName, string title)
        {
            if (string.IsNullOrEmpty(title)) return kerbalName ?? "";
            return (kerbalName ?? "") + " <color=" + StockUiRnDDecoration.ReasonColorHex + ">(" + title + ")</color>";
        }

        // ---------------- the live predicate (controls and backstops) ----------------

        /// <summary>Test seam for the vessel-continuation check. Null in production (the ERS).</summary>
        internal static Func<uint, string, bool> VesselContinuesProviderForTesting;

        /// <summary>Test seam for "a Re-Fly session is active". Null in production (the
        /// scenario's <c>ActiveReFlySessionMarker</c>).</summary>
        internal static Func<bool> ReFlySessionActiveProviderForTesting;

        // The last stand-down state logged, so the per-hover decision logs transitions only.
        private static bool? lastReFlyStandDown;

        /// <summary>True while a Re-Fly session is active; logs only when that changes.</summary>
        internal static bool ReFlySessionActive()
        {
            var seam = ReFlySessionActiveProviderForTesting;
            bool active = seam != null
                ? seam()
                : ParsekScenario.Instance?.ActiveReFlySessionMarker != null;
            if (lastReFlyStandDown != active)
            {
                lastReFlyStandDown = active;
                if (active)
                    ParsekLog.Info(BlockTag, "Re-Fly session active: EVA and crew transfer are not blocked "
                        + "until it ends (design 3.3.1; the merge settles the holds)");
                else
                    ParsekLog.Verbose(BlockTag, "No Re-Fly session: EVA and crew transfer of held kerbals are blocked again");
            }
            return active;
        }

        /// <summary>
        /// True when the vessel (pid + launch guid) is a Parsek-spawned or adopted vessel of a
        /// committed flight, judged over the Effective Recording Set.
        /// </summary>
        internal static bool VesselContinuesCommittedFlight(uint vesselPid, string vesselGuid)
        {
            var seam = VesselContinuesProviderForTesting;
            if (seam != null) return seam(vesselPid, vesselGuid);
            if (vesselPid == 0) return false;
            return CrewReservationManager.ActiveVesselIsParsekSpawned(
                EffectiveState.ComputeERS(), vesselPid, vesselGuid);
        }

        /// <summary>The decision for <paramref name="kerbalName"/> aboard the given vessel,
        /// against the live ledger. Every control and backstop reads this.</summary>
        internal static StockUiDecoration DescribeCurrent(
            string kerbalName, uint vesselPid, string vesselGuid, string tab = CrewHatchTab)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            bool refusedByDialog = StockUiCrewDialogDecoration.IsAssignmentRefused(kerbalName);
            var kind = refusedByDialog && kerbals != null
                ? kerbals.GetReservationKind(kerbalName)
                : KerbalReservationKind.NotManaged;
            bool reFly = refusedByDialog && kind == KerbalReservationKind.ReservedActive && ReFlySessionActive();
            bool continues = refusedByDialog
                && kind == KerbalReservationKind.ReservedActive
                && !reFly
                && VesselContinuesCommittedFlight(vesselPid, vesselGuid);
            bool refused = IsMoveRefused(refusedByDialog, kind, continues, reFly);
            return Decide(kerbalName, refusedByDialog, kind, continues,
                refused ? CommittedFutureIndexCache.Current : null,
                refused ? StockUiOverlayController.BuildLiveAstronautContext(null) : null,
                ReservationExplanation.DefaultDateFormatter, tab, reFly);
        }

        /// <summary>
        /// The EVA / transfer backstop. Returns false when refused, after one Info line and
        /// the same Action Blocked dialog the other reservation blocks raise, carrying the
        /// text the greyed control shows.
        /// </summary>
        internal static bool ShouldAllowMove(
            string kerbalName, uint vesselPid, string vesselGuid, FlightCrewMove move, string path)
        {
            if (string.IsNullOrEmpty(kerbalName)) return true;
            var d = DescribeCurrent(kerbalName, vesselPid, vesselGuid);
            if (!d.Blocked) return true;
            ParsekLog.Info(BlockTag,
                "Blocked " + (move == FlightCrewMove.Eva ? "EVA" : "crew transfer") + " of '" + kerbalName
                + "' via " + (path ?? "(unknown)")
                + " - held by the committed timeline (kind=" + d.Kind
                + " vesselPid=" + vesselPid.ToString(CultureInfo.InvariantCulture) + ")");
            CommittedActionDialog.ShowBlocked(BlockedTitle(move, kerbalName), d.Why, "");
            return false;
        }

        /// <summary>The live vessel's pid and launch guid, or 0 / null.</summary>
        internal static void VesselIdentity(Vessel v, out uint pid, out string guid)
        {
            pid = 0;
            guid = null;
            if (v == null) return;
            pid = v.persistentId;
            guid = v.id != Guid.Empty ? v.id.ToString("N") : null;
        }

        /// <summary>Backstop body for <c>FlightEVA.spawnEVA</c>. True = let stock proceed.</summary>
        internal static bool ShouldAllowEva(ProtoCrewMember crew, Part fromPart)
        {
            if (crew == null) return true;
            uint pid;
            string guid;
            VesselIdentity(fromPart != null ? fromPart.vessel : null, out pid, out guid);
            return ShouldAllowMove(crew.name, pid, guid, FlightCrewMove.Eva, "FlightEVA.spawnEVA");
        }

        /// <summary>Backstop body for <c>CrewTransfer.Create</c>. True = let stock proceed.</summary>
        internal static bool ShouldAllowTransfer(ProtoCrewMember crew, Part srcPart)
        {
            if (crew == null) return true;
            uint pid;
            string guid;
            VesselIdentity(srcPart != null ? srcPart.vessel : null, out pid, out guid);
            return ShouldAllowMove(crew.name, pid, guid, FlightCrewMove.Transfer, "CrewTransfer.Create");
        }

        // ---------------- portrait (KerbalPortrait.Update postfix) ----------------

        private sealed class PortraitState
        {
            internal string Kerbal;
            internal bool Blocked;
            internal string Why;
            internal float CheckedAt = float.NegativeInfinity;
        }

        private static ConditionalWeakTable<KerbalPortrait, PortraitState> portraitStates =
            new ConditionalWeakTable<KerbalPortrait, PortraitState>();

        /// <summary>
        /// After stock's <c>KerbalPortrait.Update</c> wrote the EVA button and its tooltip for a
        /// hovered IVA portrait: grey the button and put the reason in its stock tooltip when
        /// the kerbal is held. Stock rewrites both every hovered frame, so this re-applies every
        /// hovered frame from a decision re-derived at most once per
        /// <see cref="PortraitRecheckSeconds"/>. Logs only on a change of the decision.
        /// </summary>
        internal static void DecoratePortrait(KerbalPortrait portrait)
        {
            if (portrait == null) return;
            if (portrait.PortraitMode != KerbalPortraitGallery.GalleryMode.IVA) return;
            if (portrait.hoverArea == null || !portrait.hoverArea.Hover) return;
            Button eva = portrait.evaButton;
            if (eva == null) return;
            Kerbal kerbal = portrait.crewMember;
            ProtoCrewMember pcm = kerbal != null ? kerbal.protoCrewMember : null;
            if (pcm == null || string.IsNullOrEmpty(pcm.name)) return;

            PortraitState state = portraitStates.GetValue(portrait, _ => new PortraitState());
            float now = Time.unscaledTime;
            if (!string.Equals(state.Kerbal, pcm.name, StringComparison.Ordinal)
                || now - state.CheckedAt >= PortraitRecheckSeconds)
            {
                uint pid;
                string guid;
                VesselIdentity(kerbal.InPart != null ? kerbal.InPart.vessel : null, out pid, out guid);
                var d = DescribeCurrent(pcm.name, pid, guid, PortraitTab);
                bool changed = d.Blocked != state.Blocked
                    || !string.Equals(state.Kerbal, pcm.name, StringComparison.Ordinal);
                state.Kerbal = pcm.name;
                state.Blocked = d.Blocked;
                state.Why = d.Why;
                state.CheckedAt = now;
                if (changed && d.Blocked)
                    ParsekLog.Info(Tag, "Portrait EVA greyed for '" + pcm.name + "' (kind=" + d.Kind
                        + " vesselPid=" + pid.ToString(CultureInfo.InvariantCulture) + "): " + d.Why);
                else if (changed)
                    ParsekLog.Verbose(Tag, "Portrait EVA left to stock for '" + pcm.name + "' (not held)");
            }

            if (!state.Blocked) return;
            bool stockAllowed = eva.interactable;
            eva.interactable = false;
            TooltipController_Text tip = portrait.evaTooltip;
            if (tip != null)
            {
                tip.RequireInteractable = false;
                tip.textString = PortraitTooltip(stockAllowed, tip.textString, state.Why);
            }
        }

        // ---------------- crew hatch dialog (CrewHatchDialog.CreateList postfix) ----------------

        private static bool hatchMembersResolved;
        private static AccessTools.FieldRef<CrewHatchDialog, List<CrewHatchDialogWidget>> widgetsRef;
        private static AccessTools.FieldRef<CrewHatchDialogWidget, Button> btnEvaRef;
        private static AccessTools.FieldRef<CrewHatchDialogWidget, Button> btnTransferRef;
        private static AccessTools.FieldRef<CrewHatchDialogWidget, TextMeshProUGUI> nameTextRef;

        private static void ResolveHatchMembers()
        {
            if (hatchMembersResolved) return;
            hatchMembersResolved = true;
            try
            {
                if (AccessTools.Field(typeof(CrewHatchDialog), "widgets") != null)
                    widgetsRef = AccessTools.FieldRefAccess<CrewHatchDialog, List<CrewHatchDialogWidget>>("widgets");
                if (AccessTools.Field(typeof(CrewHatchDialogWidget), "btnEVA") != null)
                    btnEvaRef = AccessTools.FieldRefAccess<CrewHatchDialogWidget, Button>("btnEVA");
                if (AccessTools.Field(typeof(CrewHatchDialogWidget), "btnTransfer") != null)
                    btnTransferRef = AccessTools.FieldRefAccess<CrewHatchDialogWidget, Button>("btnTransfer");
                if (AccessTools.Field(typeof(CrewHatchDialogWidget), "textCrewName") != null)
                    nameTextRef = AccessTools.FieldRefAccess<CrewHatchDialogWidget, TextMeshProUGUI>("textCrewName");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "crew hatch dialog member lookup failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            if (widgetsRef == null || btnEvaRef == null || btnTransferRef == null || nameTextRef == null)
                ParsekLog.Warn(Tag, "crew hatch dialog members not all found (widgets=" + (widgetsRef != null)
                    + " btnEVA=" + (btnEvaRef != null) + " btnTransfer=" + (btnTransferRef != null)
                    + " textCrewName=" + (nameTextRef != null)
                    + ") - a held kerbal's hatch row keeps the parts it can set; the EVA and transfer backstops still refuse");
        }

        /// <summary>
        /// After stock built the hatch dialog's crew rows: grey EVA and Transfer on a held
        /// kerbal's row, add the status to the row's own name label and the reason to a stock
        /// tooltip on each greyed button, then log the pass. The dialog is instantiated per
        /// hatch click and destroyed on dismiss (and on any vessel change), so nothing Parsek
        /// set outlives it.
        /// </summary>
        internal static void DecorateHatchDialog(CrewHatchDialog dialog)
        {
            if (dialog == null) return;
            ResolveHatchMembers();
            List<CrewHatchDialogWidget> widgets = widgetsRef != null ? widgetsRef(dialog) : null;
            Part part = dialog.Part;
            uint pid;
            string guid;
            VesselIdentity(part != null ? part.vessel : null, out pid, out guid);

            var decorations = new List<StockUiDecoration>();
            if (widgets != null)
            {
                for (int i = 0; i < widgets.Count; i++)
                {
                    CrewHatchDialogWidget w = widgets[i];
                    ProtoCrewMember pcm = w != null ? w.protoCrewMember : null;
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    var d = DescribeCurrent(pcm.name, pid, guid, CrewHatchTab);
                    decorations.Add(d);
                    if (!d.Blocked) continue;
                    try
                    {
                        GreyWithReason(dialog, btnEvaRef != null ? btnEvaRef(w) : null, d.Why);
                        GreyWithReason(dialog, btnTransferRef != null ? btnTransferRef(w) : null, d.Why);
                        TextMeshProUGUI label = nameTextRef != null ? nameTextRef(w) : null;
                        if (label != null) label.text = HatchRowLabel(pcm.name, d.Title);
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.WarnRateLimited(Tag, "crew-hatch-row-failed",
                            "crew hatch row annotation failed for " + pcm.name + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                    }
                }
            }
            StockUiDecorationQuery.LogPass(StockUiScreen.FlightCrew, new[] { CrewHatchTab }, decorations);
        }

        private static void GreyWithReason(Component scope, Button button, string why)
        {
            if (button == null || !button.gameObject.activeSelf) return;
            button.interactable = false;
            if (string.IsNullOrEmpty(why)) return;
            var tip = button.GetComponent<TooltipController_Text>();
            bool owned = false;
            if (tip == null)
            {
                var prefab = StockUiFacilityDecoration.FindTooltipPrefab(scope, "CrewHatch");
                if (prefab == null) return;
                tip = button.gameObject.AddComponent<TooltipController_Text>();
                tip.prefab = prefab;
                owned = true;
            }
            else if (tip.prefab == null)
            {
                var prefab = StockUiFacilityDecoration.FindTooltipPrefab(scope, "CrewHatch");
                if (prefab != null) tip.prefab = prefab;
            }
            tip.RequireInteractable = false;
            tip.SetText(StockUiFacilityDecoration.ComposeTooltipText(owned, tip.textString,
                StockUiFacilityDecoration.WrapTooltipText(why), "crew hatch button tooltip"));
            tip.enabled = true;
        }

        internal static void ResetForTesting()
        {
            portraitStates = new ConditionalWeakTable<KerbalPortrait, PortraitState>();
            VesselContinuesProviderForTesting = null;
            ReFlySessionActiveProviderForTesting = null;
            lastReFlyStandDown = null;
        }
    }
}
