using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>What the crew dialog does to one available-crew row's look.</summary>
    internal enum CrewDialogRowLook
    {
        /// <summary>Not refused and never greyed by Parsek: stock's look stays as stock set it.</summary>
        LeaveStock,
        /// <summary>Refused: stock's inactive-crew look plus the locked-with-reason tooltip.</summary>
        Grey,
        /// <summary>Greyed by Parsek earlier but no longer refused: put stock's look back.</summary>
        RestoreStock
    }

    /// <summary>
    /// VAB/SPH crew assignment dialog annotation on stock mechanisms
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 5, owner
    /// ruling R3). A kerbal the committed timeline reserves is LISTED in the available-crew
    /// list with stock's own <c>crew.inactive</c> look (disabled sprite, greyed name and
    /// trait, faded portrait, dragging off, hover off) and stock's locked-with-reason
    /// tooltip (<c>CrewListItem.SetButtonEnabled(false, title, why)</c>), instead of being
    /// hidden. The refusal predicate is <see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>,
    /// the one the old hiding filter used, so every kerbal that was hidden is exactly the
    /// kerbal that is greyed and refused now (the pairing rule). Every seat-placing path
    /// reads the same predicate (<see cref="ShouldAllowAssignment"/>), never the row's look.
    /// </summary>
    internal static class StockUiCrewDialogDecoration
    {
        private const string Tag = "StockUiOverlay";
        private const string BlockTag = "CrewDialog";

        /// <summary>The title used when a refused kerbal has no reservation kind to name
        /// (unreachable while the predicate is reserved-or-retired, kept so a refusal never
        /// reads blank).</summary>
        internal const string FallbackTitle = "Reserved";

        private sealed class RowState
        {
            internal bool Greyed;
            internal bool StockDragEnabled;
            internal bool StockMouseover;
            internal Sprite StockBackground;
            internal Sprite StockNormal;
            internal Sprite StockHover;
            internal bool HasNameColor;
            internal Color StockNameColor;
            internal bool HasTraitColor;
            internal Color StockTraitColor;
            internal bool HasSpriteColor;
            internal Color StockSpriteColor;
        }

        private static ConditionalWeakTable<CrewListItem, RowState> rowStates =
            new ConditionalWeakTable<CrewListItem, RowState>();

        // ---------------- pure decisions ----------------

        /// <summary>
        /// The decision for one kerbal the dialog lists. <paramref name="refused"/> is
        /// <see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>; the kind picks the text:
        /// a kerbal a committed flight holds reads the Astronaut Complex's on-flight / lost
        /// explanation, a retired stand-in reads <see cref="ReservationExplanation.KerbalRetiredStandIn"/>.
        /// A refused kerbal is always Blocked, whatever the kind, so nothing the old filter
        /// hid becomes assignable.
        /// </summary>
        internal static StockUiDecoration Decide(
            string kerbalName,
            bool refused,
            KerbalReservationKind kind,
            CommittedFutureIndex index,
            AstronautComplexContext context,
            Func<double, string> formatDate)
        {
            var d = new StockUiDecoration
            {
                Screen = StockUiScreen.CrewAssignment,
                Tab = StockUiDecorationQuery.CrewAssignmentAvailableTab,
                Kind = StockUiDecorationKind.None,
                Id = kerbalName,
                UT = double.NaN
            };
            if (!refused || string.IsNullOrEmpty(kerbalName)) return d;

            context = context ?? new AstronautComplexContext();
            ReservationText text;
            if (kind == KerbalReservationKind.ReservedActive)
            {
                var reservation = context.Reservation != null ? context.Reservation(kerbalName) : null;
                string owner = context.SlotOwner != null ? context.SlotOwner(kerbalName) : null;
                text = StockUiReservationPredicates.ExplainKerbalReservation(
                    index, kerbalName, reservation, owner, context.IsLoopingRecording, formatDate);
                d.Kind = KerbalsModule.IsLossHold(reservation)
                    ? StockUiDecorationKind.KerbalLost
                    : StockUiDecorationKind.KerbalOnFlight;
            }
            else if (kind == KerbalReservationKind.ReservedRetired)
            {
                text = ReservationExplanation.KerbalRetiredStandIn(
                    context.SlotOwner != null ? context.SlotOwner(kerbalName) : null);
                d.Kind = StockUiDecorationKind.KerbalRetiredStandIn;
            }
            else
            {
                text = new ReservationText
                {
                    Title = FallbackTitle,
                    Fact = Patches.KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.ReservedActive)
                };
                d.Kind = StockUiDecorationKind.KerbalOnFlight;
            }
            d.Marked = true;
            d.Blocked = true;
            d.Title = string.IsNullOrEmpty(text.Title) ? FallbackTitle : text.Title;
            d.Why = text.Body;
            return d;
        }

        /// <summary>
        /// Grey a refused row; put stock's look back on a row Parsek greyed that is no
        /// longer refused; otherwise leave it. Every build re-derives this for every row, so
        /// a greyed look cannot outlive the reservation that caused it.
        /// </summary>
        internal static CrewDialogRowLook DecideLook(bool blocked, bool greyedByParsek)
        {
            if (blocked) return CrewDialogRowLook.Grey;
            return greyedByParsek ? CrewDialogRowLook.RestoreStock : CrewDialogRowLook.LeaveStock;
        }

        // ---------------- the live predicate (decoration and every backstop) ----------------

        /// <summary>
        /// The decision for <paramref name="kerbalName"/> against the live ledger. The row
        /// decoration and every seat-placing backstop read this, so a greyed row and a
        /// refused placement cannot disagree, and they say the same thing.
        /// </summary>
        internal static StockUiDecoration DescribeCurrent(string kerbalName)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            bool refused = IsAssignmentRefused(kerbalName);
            var kind = refused && kerbals != null
                ? kerbals.GetReservationKind(kerbalName)
                : KerbalReservationKind.NotManaged;
            return Decide(kerbalName, refused,
                kind,
                refused ? CommittedFutureIndexCache.Current : null,
                refused ? StockUiOverlayController.BuildLiveAstronautContext(null) : null,
                ReservationExplanation.DefaultDateFormatter);
        }

        /// <summary>The refusal predicate alone: the one the old hiding filter used.</summary>
        internal static bool IsAssignmentRefused(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            var kerbals = LedgerOrchestrator.Kerbals;
            return kerbals != null && kerbals.ShouldFilterFromCrewDialog(kerbalName);
        }

        /// <summary>
        /// The seat-placement backstop, shared by the click (<c>MoveCrewToEmptySeat</c>) and
        /// drag-drop (<c>DropOnCrewList</c>) prefixes. Returns false when refused, after the
        /// log line and the same Action Blocked dialog the other reservation blocks raise,
        /// carrying the text the greyed row's tooltip shows.
        /// </summary>
        internal static bool ShouldAllowAssignment(string kerbalName, string path)
        {
            if (string.IsNullOrEmpty(kerbalName)) return true;
            var d = DescribeCurrent(kerbalName);
            if (!d.Blocked) return true;
            ParsekLog.Info(BlockTag,
                "Blocked crew assignment of '" + kerbalName + "' via " + path
                + " - reserved by the committed timeline (kind=" + d.Kind + ")");
            CommittedActionDialog.ShowBlocked("Cannot assign \"" + kerbalName + "\"", d.Why, "");
            return false;
        }

        // ---------------- live applicators (Harmony patch bodies) ----------------

        private static bool stockMembersResolved;
        private static AccessTools.FieldRef<BaseCrewAssignmentDialog, Sprite> disabledSpriteRef;
        private static Color disabledColor = new Color(1f, 1f, 1f, 0.5f);
        private static FieldInfo kerbalNameField;
        private static FieldInfo xpTraitField;
        private static MethodInfo moveCrewToEmptySeat;
        private static bool fillFallbackWarned;

        private static void ResolveStockMembers()
        {
            if (stockMembersResolved) return;
            stockMembersResolved = true;
            try
            {
                if (AccessTools.Field(typeof(BaseCrewAssignmentDialog), "disabledCrewListSprite") != null)
                    disabledSpriteRef = AccessTools.FieldRefAccess<BaseCrewAssignmentDialog, Sprite>("disabledCrewListSprite");
                FieldInfo color = AccessTools.Field(typeof(BaseCrewAssignmentDialog), "disabledColor");
                if (color != null && color.IsStatic && color.FieldType == typeof(Color))
                    disabledColor = (Color)color.GetValue(null);
                kerbalNameField = typeof(CrewListItem).GetField("kerbalName", BindingFlags.Instance | BindingFlags.Public);
                xpTraitField = typeof(CrewListItem).GetField("xp_trait", BindingFlags.Instance | BindingFlags.Public);
                moveCrewToEmptySeat = ResolveMoveCrewToEmptySeat();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "crew dialog stock member lookup failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            if (disabledSpriteRef == null || kerbalNameField == null || xpTraitField == null)
                ParsekLog.Warn(Tag, "crew dialog inactive-look members not all found (sprite="
                    + (disabledSpriteRef != null) + " kerbalName=" + (kerbalNameField != null)
                    + " xp_trait=" + (xpTraitField != null)
                    + ") - a reserved row keeps the parts it can set; the seat backstops still refuse");
        }

        internal static MethodInfo ResolveMoveCrewToEmptySeat()
        {
            return AccessTools.Method(typeof(BaseCrewAssignmentDialog), "MoveCrewToEmptySeat",
                new[] { typeof(UIList), typeof(UIList), typeof(UIListItem), typeof(int) });
        }

        /// <summary>
        /// <c>AddAvailItem(ProtoCrewMember, out CrewListItem, ...)</c> postfix: decorate the
        /// row stock just built. Stock has already applied its own inactive look (if any),
        /// so the captured stock state is whatever stock showed.
        /// </summary>
        internal static void DecorateAddedRow(BaseCrewAssignmentDialog dialog, CrewListItem row, ProtoCrewMember crew)
        {
            if (row == null || crew == null || string.IsNullOrEmpty(crew.name)) return;
            ApplyRow(dialog, row, DescribeCurrent(crew.name));
        }

        /// <summary>
        /// <c>CreateAvailList</c> postfix: re-derive every available row (grey, restore or
        /// leave) and log the pass once
        /// (<c>decorate screen=CrewAssignment tab=Available items=N marked=M blocked=B</c>,
        /// plus one Verbose line per greyed kerbal).
        /// </summary>
        internal static void DecorateAvailList(BaseCrewAssignmentDialog dialog, string reason)
        {
            if (dialog == null) return;
            UIList list = dialog.scrollListAvail;
            var decorations = new List<StockUiDecoration>();
            int greyed = 0, restored = 0;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    CrewListItem row = RowAt(list, i);
                    ProtoCrewMember crew = SafeCrew(row);
                    if (row == null || crew == null || string.IsNullOrEmpty(crew.name)) continue;
                    var d = DescribeCurrent(crew.name);
                    var look = ApplyRow(dialog, row, d);
                    if (look == CrewDialogRowLook.Grey) greyed++;
                    else if (look == CrewDialogRowLook.RestoreStock) restored++;
                    decorations.Add(d);
                }
            }
            ParsekLog.Verbose(Tag, "Crew assignment decoration pass (" + (reason ?? "build") + ")"
                + " greyed=" + greyed + " restored=" + restored);
            StockUiDecorationQuery.LogPass(StockUiScreen.CrewAssignment,
                new[] { StockUiDecorationQuery.CrewAssignmentAvailableTab }, decorations);
        }

        private static CrewDialogRowLook ApplyRow(BaseCrewAssignmentDialog dialog, CrewListItem row, StockUiDecoration d)
        {
            RowState state = rowStates.GetValue(row, _ => new RowState());
            var look = DecideLook(d.Blocked, state.Greyed);
            try
            {
                if (look == CrewDialogRowLook.Grey)
                {
                    if (!state.Greyed) Capture(row, state);
                    ApplyInactiveLook(dialog, row);
                    row.SetButtonEnabled(false, d.Title, d.Why);
                    state.Greyed = true;
                }
                else if (look == CrewDialogRowLook.RestoreStock)
                {
                    Restore(row, state);
                    state.Greyed = false;
                    ParsekLog.Verbose(Tag, "crew dialog row restored to stock look for " + d.Id + " (no longer reserved)");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "crew-dialog-row-failed",
                    "crew dialog row annotation failed for " + d.Id + " (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            return look;
        }

        /// <summary>Stock's <c>crew.inactive</c> look, member for member
        /// (<c>BaseCrewAssignmentDialog.AddAvailItem(PCM, out CrewListItem, ...)</c>).</summary>
        private static void ApplyInactiveLook(BaseCrewAssignmentDialog dialog, CrewListItem row)
        {
            ResolveStockMembers();
            UIDragPanel drag = row.GetComponent<UIDragPanel>();
            if (drag != null) drag.dragEnabled = false;
            Sprite sprite = dialog != null && disabledSpriteRef != null ? disabledSpriteRef(dialog) : null;
            UIHoverPanel hover = row.GetComponent<UIHoverPanel>();
            if (hover != null && sprite != null)
            {
                hover.backgroundHover = sprite;
                hover.backgroundNormal = sprite;
                if (hover.backgroundImage != null) hover.backgroundImage.sprite = sprite;
            }
            SetGraphicColor(kerbalNameField, row, Color.grey);
            SetGraphicColor(xpTraitField, row, Color.grey);
            if (row.kerbalSprite != null) row.kerbalSprite.color = disabledColor;
            row.MouseoverEnabled = false;
        }

        private static void Capture(CrewListItem row, RowState state)
        {
            ResolveStockMembers();
            UIDragPanel drag = row.GetComponent<UIDragPanel>();
            state.StockDragEnabled = drag == null || drag.dragEnabled;
            state.StockMouseover = row.MouseoverEnabled;
            UIHoverPanel hover = row.GetComponent<UIHoverPanel>();
            if (hover != null)
            {
                state.StockNormal = hover.backgroundNormal;
                state.StockHover = hover.backgroundHover;
                state.StockBackground = hover.backgroundImage != null ? hover.backgroundImage.sprite : null;
            }
            state.HasNameColor = TryGetGraphicColor(kerbalNameField, row, out state.StockNameColor);
            state.HasTraitColor = TryGetGraphicColor(xpTraitField, row, out state.StockTraitColor);
            state.HasSpriteColor = row.kerbalSprite != null;
            if (state.HasSpriteColor) state.StockSpriteColor = row.kerbalSprite.color;
        }

        private static void Restore(CrewListItem row, RowState state)
        {
            UIDragPanel drag = row.GetComponent<UIDragPanel>();
            if (drag != null) drag.dragEnabled = state.StockDragEnabled;
            UIHoverPanel hover = row.GetComponent<UIHoverPanel>();
            if (hover != null)
            {
                hover.backgroundNormal = state.StockNormal;
                hover.backgroundHover = state.StockHover;
                if (hover.backgroundImage != null) hover.backgroundImage.sprite = state.StockBackground;
            }
            if (state.HasNameColor) SetGraphicColor(kerbalNameField, row, state.StockNameColor);
            if (state.HasTraitColor) SetGraphicColor(xpTraitField, row, state.StockTraitColor);
            if (state.HasSpriteColor && row.kerbalSprite != null) row.kerbalSprite.color = state.StockSpriteColor;
            row.MouseoverEnabled = state.StockMouseover;
            ProtoCrewMember crew = row.GetCrewRef();
            if (crew != null) row.SetTooltip(crew);
        }

        private static bool TryGetGraphicColor(FieldInfo field, CrewListItem row, out Color color)
        {
            color = Color.white;
            Graphic g = field != null ? field.GetValue(row) as Graphic : null;
            if (g == null) return false;
            color = g.color;
            return true;
        }

        private static void SetGraphicColor(FieldInfo field, CrewListItem row, Color color)
        {
            Graphic g = field != null ? field.GetValue(row) as Graphic : null;
            if (g != null) g.color = color;
        }

        /// <summary>
        /// <c>ButtonFill</c> prefix. Stock fills every empty seat from the TOP of the
        /// available list, so one greyed kerbal at the top would stop the fill (the seat
        /// backstop refuses him on every seat). When at least one listed kerbal is refused,
        /// this runs stock's loop over the first NOT-refused row instead and returns false;
        /// otherwise it returns true and stock's fill runs unchanged. The move goes through
        /// the virtual <c>MoveCrewToEmptySeat</c>, so the editor dialog's override still
        /// updates the ship manifest and rebuilds the lists after each seat.
        /// </summary>
        internal static bool FillSkippingRefused(BaseCrewAssignmentDialog dialog)
        {
            if (dialog == null) return true;
            UIList avail = dialog.scrollListAvail;
            UIList seats = dialog.scrollListCrew;
            if (avail == null || seats == null) return true;
            int refusedRows = CountRefusedRows(avail);
            if (refusedRows == 0) return true;

            ResolveStockMembers();
            if (moveCrewToEmptySeat == null)
            {
                if (!fillFallbackWarned)
                {
                    fillFallbackWarned = true;
                    ParsekLog.Warn(BlockTag, "BaseCrewAssignmentDialog.MoveCrewToEmptySeat not found - Fill runs stock's loop, "
                        + "and the seat backstop refuses each reserved kerbal it reaches");
                }
                return true;
            }

            int placed = 0;
            int seat = 0;
            for (int guard = 0; guard < 1000; guard++)
            {
                UIListItem next = FirstAssignableRow(avail);
                if (next == null) break;
                UIListItem seatItem = seats.GetUilistItemAt(++seat);
                if (seatItem == null) break;
                CrewListItem seatRow = seatItem.GetComponent<CrewListItem>();
                if (seatRow == null || !seatRow.isEmpty) continue;
                moveCrewToEmptySeat.Invoke(dialog, new object[] { avail, seats, next, seat });
                placed++;
            }
            ParsekLog.Info(BlockTag, "Fill skipped " + refusedRows + " reserved kerbal(s) in the available list: placed=" + placed);
            return false;
        }

        private static int CountRefusedRows(UIList list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ProtoCrewMember crew = SafeCrew(RowAt(list, i));
                if (crew != null && IsAssignmentRefused(crew.name)) n++;
            }
            return n;
        }

        private static UIListItem FirstAssignableRow(UIList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                UIListItem item = SafeItemAt(list, i);
                if (item == null) continue;
                CrewListItem row = item.GetComponent<CrewListItem>();
                ProtoCrewMember crew = SafeCrew(row);
                if (crew != null && IsAssignmentRefused(crew.name)) continue;
                return item;
            }
            return null;
        }

        /// <summary>The kerbal a dragged or clicked list item carries, or null.</summary>
        internal static string KerbalNameOf(UIListItem item)
        {
            if (item == null) return null;
            CrewListItem row = item.GetComponent<CrewListItem>();
            ProtoCrewMember crew = SafeCrew(row);
            return crew != null ? crew.name : null;
        }

        private static UIListItem SafeItemAt(UIList list, int index)
        {
            try
            {
                return list.GetUilistItemAt(index);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "crew-dialog-item-at-failed",
                    "crew dialog row lookup failed (" + ex.GetType().Name + ")");
                return null;
            }
        }

        private static CrewListItem RowAt(UIList list, int index)
        {
            UIListItem item = SafeItemAt(list, index);
            return item != null ? item.GetComponent<CrewListItem>() : null;
        }

        private static ProtoCrewMember SafeCrew(CrewListItem row)
        {
            return row != null ? row.GetCrewRef() : null;
        }

        internal static void ResetForTesting()
        {
            rowStates = new ConditionalWeakTable<CrewListItem, RowState>();
            fillFallbackWarned = false;
        }
    }
}
