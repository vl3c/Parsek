using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// What one Astronaut Complex row shows: its stock label text, and whether its action
    /// button (hire on an applicant, dismiss on an available kerbal) is in stock's
    /// "locked with reason" state (<c>CrewListItem.SetButtonEnabled(false, title,
    /// caption)</c>).
    /// </summary>
    internal struct CrewRowDecision
    {
        /// <summary>The label to show, or null to leave the stock label.</summary>
        internal string Label;
        internal bool DisableButton;
        internal string DisabledTitle;
        internal string DisabledCaption;
        /// <summary><c>hire</c>, <c>dismiss</c> or null.</summary>
        internal string BlockKind;
    }

    /// <summary>Which of a crew row's own stock text lines carries the Parsek status.</summary>
    internal enum CrewStatusSurface
    {
        /// <summary>Nothing visible to write to: the row stays unlabelled (logged).</summary>
        None,
        /// <summary>The row's status line (<c>CrewListItem.label</c>, <c>SetLabel</c>).</summary>
        Label,
        /// <summary>The trait line (<c>CrewListItem.xp_trait</c>): an applicant row's only
        /// line under the name, because the applicant prefab hides <c>label</c> (stock's own
        /// <c>SetLabel("For Hire")</c> never shows either).</summary>
        Trait
    }

    /// <summary>
    /// Astronaut Complex annotation on stock mechanisms (docs/dev/research/
    /// stock-ui-reservation-overlays-2026-09-25.md, section 5). Every marked kerbal gets
    /// its row's own stock label (<c>CrewListItem.SetLabel</c>; on an applicant row, whose
    /// prefab hides that label, the trait line under the name, see
    /// <see cref="ChooseStatusSurface"/>). A clickable block puts
    /// the button in stock's locked-with-reason state and appends the explanation to the
    /// stock crew tooltip: hire for an applicant a committed future hires (the
    /// <c>KerbalHirePatch</c> predicate), dismiss for a kerbal Parsek manages (the
    /// <c>KerbalDismissalPatch</c> predicate). Informational kinds (lost, retired
    /// stand-in, future dismissal) change the label only; an active stand-in's label
    /// (<c>Stand-in for &lt;owner&gt;</c>) sits on a row whose dismiss button the same
    /// dismissal predicate locks. The Harmony postfixes are
    /// scene-independent, so the complex opened from the VAB/SPH crew dialog is decorated
    /// the same way as the Space Center one.
    /// </summary>
    internal static class StockUiAstronautDecoration
    {
        private const string Tag = "StockUiOverlay";

        /// <summary>The disabled-button title for a dismissal refused on an unmarked kerbal
        /// (a stand-in, or a kerbal a committed flight names).</summary>
        internal const string DismissBlockedTitle = "Managed by Parsek";

        private sealed class RowState
        {
            internal string StockLabel;
            internal bool LabelChanged;
            internal string StockTrait;
            internal bool TraitChanged;
            internal bool ButtonDisabledByParsek;
            internal bool SurfaceLogged;
        }

        private static ConditionalWeakTable<CrewListItem, RowState> rowStates =
            new ConditionalWeakTable<CrewListItem, RowState>();

        /// <summary>
        /// The row decision. <paramref name="rowActionable"/> is whether the row's button
        /// is live (stock leaves a tourist, an editor-manifest kerbal and an applicant
        /// over the crew limit without one: their stock lock stays as stock set it).
        /// <paramref name="dismissalRefusal"/> is the dismissal block's explanation, or null
        /// when dismissal is allowed.
        /// </summary>
        internal static CrewRowDecision Decide(
            StockUiDecoration d, string tab, string stockLabel, bool rowActionable, string dismissalRefusal)
        {
            var r = new CrewRowDecision();
            if (d.Marked && !string.IsNullOrEmpty(d.Title))
            {
                r.Label = tab == StockUiDecorationQuery.AstronautAssignedTab
                    ? ComposeAssignedLabel(stockLabel, d.Title)
                    : d.Title;
            }
            if (!rowActionable) return r;

            if (tab == StockUiDecorationQuery.AstronautApplicantsTab
                && d.Kind == StockUiDecorationKind.KerbalHire)
            {
                r.DisableButton = true;
                r.DisabledTitle = d.Title;
                r.DisabledCaption = d.Why;
                r.BlockKind = "hire";
            }
            else if (tab == StockUiDecorationQuery.AstronautAvailableTab && dismissalRefusal != null)
            {
                r.DisableButton = true;
                r.DisabledTitle = d.Marked && !string.IsNullOrEmpty(d.Title) ? d.Title : DismissBlockedTitle;
                // An active stand-in's why is the refusal plus, when it shares its owner's
                // seat in the active-crew count, the one seat sentence (ForAstronautComplex).
                r.DisabledCaption = d.Kind == StockUiDecorationKind.KerbalStandIn && !string.IsNullOrEmpty(d.Why)
                    ? d.Why
                    : dismissalRefusal;
                r.BlockKind = "dismiss";
            }
            return r;
        }

        /// <summary>
        /// Where a row's status goes: its stock status line when that line is shown, else,
        /// on an APPLICANT row only, the trait line (the applicant prefab hides the status
        /// line, so writing it changes nothing on screen - the GUI-28 census finding F2).
        /// Any other row with a hidden status line stays unlabelled rather than overwrite
        /// the trait column.
        /// </summary>
        internal static CrewStatusSurface ChooseStatusSurface(string tab, bool labelShown, bool traitPresent)
        {
            if (labelShown) return CrewStatusSurface.Label;
            if (tab == StockUiDecorationQuery.AstronautApplicantsTab && traitPresent) return CrewStatusSurface.Trait;
            return CrewStatusSurface.None;
        }

        /// <summary>
        /// Why a stock text is or is not on screen inside its row: <c>shown</c>,
        /// <c>missing</c> (no such text), <c>disabled</c> (the component is off),
        /// <c>transparent</c> (alpha 0) or <c>inactive:&lt;object&gt;</c> (it or a parent
        /// up to the row root is deactivated). Parents above the row are not read: a row
        /// built while the complex is hidden is still judged by its own prefab.
        /// </summary>
        internal static string DescribeVisibilityWithinRow(object text, Transform rowRoot)
        {
            // "is", not "== null", and the Unity reads in a separate non-inlined core: the
            // no-text answer must not JIT or call a Unity native (headless-testable).
            if (!(text is Component)) return "missing";
            return DescribeLiveVisibilityWithinRow((Component)text, rowRoot);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string DescribeLiveVisibilityWithinRow(Component comp, Transform rowRoot)
        {
            object text = comp;
            Behaviour behaviour = text as Behaviour;
            if (behaviour != null && !behaviour.enabled) return "disabled";
            Graphic graphic = text as Graphic;
            if (graphic != null && graphic.color.a <= 0.01f) return "transparent";
            for (Transform t = comp.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return "inactive:" + t.name;
                if (t == rowRoot) break;
            }
            return "shown";
        }

        /// <summary>The status text a row shows (its status line, else an applicant's trait
        /// line): what the player reads, for the in-game cells.</summary>
        internal static string ShownStatusText(CrewListItem row)
        {
            if (row == null) return "";
            object label = StockUiText.LabelField(row, typeof(CrewListItem), "label");
            if (DescribeVisibilityWithinRow(label, row.transform) == "shown")
                return StockUiText.Get(label) ?? "";
            object trait = StockUiText.LabelField(row, typeof(CrewListItem), "xp_trait");
            return StockUiText.Get(trait) ?? "";
        }

        /// <summary>An Assigned row's stock label names the vessel and seat, so the status is
        /// appended to it rather than replacing it.</summary>
        internal static string ComposeAssignedLabel(string stockLabel, string title)
        {
            if (string.IsNullOrEmpty(stockLabel)) return title;
            return stockLabel + " (" + title + ")";
        }

        /// <summary>Whether the crew tooltip carries the explanation: only the kinds whose row
        /// has a clickable block (a future hire, a kerbal a committed flight holds).</summary>
        internal static bool AppendsTooltip(StockUiDecorationKind kind)
        {
            return kind == StockUiDecorationKind.KerbalHire || kind == StockUiDecorationKind.KerbalOnFlight;
        }

        /// <summary>
        /// Whether the crew tooltip postfix touches this kerbal's tooltip at all. The same
        /// <c>TooltipController_CrewAC</c> serves the VAB/SPH crew assignment dialog, so a
        /// kerbal without a clickable-kind mark keeps stock's tooltip exactly (text and
        /// <c>showTooltip</c>) in every screen.
        /// </summary>
        internal static bool ShouldAnnotateCrewTooltip(StockUiDecoration d)
        {
            return d.Marked && AppendsTooltip(d.Kind) && !string.IsNullOrEmpty(d.Why);
        }

        /// <summary>
        /// Appends the explanation to a stock crew tooltip in stock's own reason format
        /// (<c>"\n\n&lt;b&gt;title&lt;/b&gt;\ncaption"</c>, the same block stock appends for
        /// the crew-limit lock). Idempotent.
        /// </summary>
        internal static string AppendTooltip(string description, string title, string why)
        {
            if (string.IsNullOrEmpty(why)) return description;
            if (!string.IsNullOrEmpty(description) && description.Contains(why)) return description;
            string block = "<b>" + (title ?? "") + "</b>\n" + why;
            return string.IsNullOrEmpty(description) ? block : description + "\n\n" + block;
        }

        // ---------------- live applicators (Harmony postfix bodies) ----------------

        internal static string TabFor(string addItemMethodName)
        {
            switch (addItemMethodName)
            {
                case "AddItem_Applicants": return StockUiDecorationQuery.AstronautApplicantsTab;
                case "AddItem_Available": return StockUiDecorationQuery.AstronautAvailableTab;
                case "AddItem_Assigned": return StockUiDecorationQuery.AstronautAssignedTab;
                case "AddItem_Kia": return StockUiDecorationQuery.AstronautKiaTab;
                default: return null;
            }
        }

        internal static UIList ListFor(AstronautComplex complex, string tab)
        {
            if (complex == null) return null;
            switch (tab)
            {
                case StockUiDecorationQuery.AstronautApplicantsTab: return complex.ScrollListApplicants;
                case StockUiDecorationQuery.AstronautAvailableTab: return complex.ScrollListAvailable;
                case StockUiDecorationQuery.AstronautAssignedTab: return complex.ScrollListAssigned;
                case StockUiDecorationQuery.AstronautKiaTab: return complex.ScrollListKia;
                default: return null;
            }
        }

        private static readonly string[] Tabs =
        {
            StockUiDecorationQuery.AstronautApplicantsTab,
            StockUiDecorationQuery.AstronautAvailableTab,
            StockUiDecorationQuery.AstronautAssignedTab,
            StockUiDecorationQuery.AstronautKiaTab
        };

        /// <summary>
        /// <c>AstronautComplex.AddItem_*</c> postfix. The methods return void and append the
        /// new row last, so the row is the list's last item; when a mod has reordered the
        /// list the row is found by the kerbal's name instead.
        /// </summary>
        internal static void DecorateAddedRow(AstronautComplex complex, string tab, ProtoCrewMember crew)
        {
            if (complex == null || crew == null || string.IsNullOrEmpty(tab)) return;
            UIList list = ListFor(complex, tab);
            if (list == null || list.Count == 0) return;
            CrewListItem row = RowAt(list, list.Count - 1);
            if (row == null || !string.Equals(SafeName(row), crew.name, StringComparison.Ordinal))
                row = FindRowByName(list, crew.name);
            if (row == null)
            {
                ParsekLog.VerboseRateLimited(Tag, "ac-added-row-missing",
                    "Astronaut Complex " + tab + " row for " + crew.name + " not found after AddItem - not annotated");
                return;
            }
            DecorateRow(row, tab, crew.name, StockUiLiveSnapshot.Current, null);
        }

        /// <summary>
        /// <c>AstronautComplex.UpdateCrewCounts</c> postfix: stock re-unlocks every applicant
        /// there (<c>SetApplicantsListUnlocked</c>), so every row is re-decorated after it,
        /// and the pass is logged once per tab.
        /// </summary>
        internal static void DecorateAllRows(AstronautComplex complex, string reason)
        {
            if (complex == null) return;
            var snapshot = StockUiLiveSnapshot.Current;
            var decorations = new List<StockUiDecoration>();
            var counts = new DecorateCounts();
            for (int t = 0; t < Tabs.Length; t++)
            {
                UIList list = ListFor(complex, Tabs[t]);
                if (list == null) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                {
                    CrewListItem row = RowAt(list, i);
                    string name = SafeName(row);
                    if (row == null || string.IsNullOrEmpty(name)) continue;
                    var d = DecorateRow(row, Tabs[t], name, snapshot, counts);
                    if (seen.Add(name)) decorations.Add(d);
                }
            }
            ParsekLog.Verbose(Tag, "Astronaut Complex decoration pass (" + (reason ?? "refresh") + ")"
                + " labelled=" + counts.Labelled + " hireDisabled=" + counts.HireDisabled
                + " dismissDisabled=" + counts.DismissDisabled + " restored=" + counts.Restored);
            StockUiDecorationQuery.LogPass(StockUiScreen.AstronautComplex, Tabs, decorations);
        }

        private sealed class DecorateCounts
        {
            internal int Labelled;
            internal int HireDisabled;
            internal int DismissDisabled;
            internal int Restored;
        }

        private static StockUiDecoration DecorateRow(
            CrewListItem row, string tab, string name, StockUiLiveSnapshot snapshot, DecorateCounts counts)
        {
            var d = snapshot.Kerbal(name, tab);
            try
            {
                RowState state = rowStates.GetValue(row, _ => new RowState());
                object label = StockUiText.LabelField(row, typeof(CrewListItem), "label");
                object trait = StockUiText.LabelField(row, typeof(CrewListItem), "xp_trait");
                string current = StockUiText.Get(label);
                string currentTrait = StockUiText.Get(trait);
                if (!state.LabelChanged)
                    state.StockLabel = current;
                if (!state.TraitChanged)
                    state.StockTrait = currentTrait;

                string labelVisibility = DescribeVisibilityWithinRow(label, row.transform);
                var surface = ChooseStatusSurface(tab, labelVisibility == "shown", trait != null);

                bool actionable = state.ButtonDisabledByParsek || row.MouseoverEnabled;
                // The same predicate the record reads (AstronautComplexContext.DismissalRefusal).
                string dismissalRefusal = tab == StockUiDecorationQuery.AstronautAvailableTab
                    ? Patches.KerbalDismissalPatch.DescribeDismissalRefusal(LedgerOrchestrator.Kerbals, name)
                    : null;
                string stockText = surface == CrewStatusSurface.Trait ? state.StockTrait : state.StockLabel;
                var decision = Decide(d, tab, stockText, actionable, dismissalRefusal);
                string wantLabel = surface == CrewStatusSurface.Label ? decision.Label : null;
                string wantTrait = surface == CrewStatusSurface.Trait ? decision.Label : null;

                if (decision.Label != null && !state.SurfaceLogged)
                {
                    state.SurfaceLogged = true;
                    ParsekLog.Verbose(Tag, "Astronaut Complex " + tab + " row for " + name + " status surface="
                        + surface + " (stock label " + labelVisibility + ")");
                }

                if (wantLabel != null)
                {
                    if (!string.Equals(current, wantLabel, StringComparison.Ordinal))
                        row.SetLabel(wantLabel);
                    state.LabelChanged = true;
                    if (counts != null) counts.Labelled++;
                }
                else if (state.LabelChanged)
                {
                    row.SetLabel(state.StockLabel ?? "");
                    state.LabelChanged = false;
                    if (counts != null) counts.Restored++;
                }

                if (wantTrait != null)
                {
                    if (!string.Equals(currentTrait, wantTrait, StringComparison.Ordinal))
                        StockUiText.Set(trait, wantTrait);
                    state.TraitChanged = true;
                    if (counts != null) counts.Labelled++;
                }
                else if (state.TraitChanged)
                {
                    StockUiText.Set(trait, state.StockTrait ?? "");
                    state.TraitChanged = false;
                    if (counts != null) counts.Restored++;
                }

                if (decision.Label != null && surface == CrewStatusSurface.None)
                    ParsekLog.VerboseRateLimited(Tag, "ac-no-status-surface",
                        "Astronaut Complex " + tab + " row for " + name + " has no visible status line (stock label "
                        + labelVisibility + ") - status '" + decision.Label + "' not shown");

                if (decision.DisableButton)
                {
                    if (GameStateRecorder.IsReplayingActions)
                    {
                        ParsekLog.Verbose(Tag, "Astronaut Complex " + decision.BlockKind
                            + " button block bypassed for " + name + " - action replay in progress");
                    }
                    else
                    {
                        row.SetButtonEnabled(false, decision.DisabledTitle, decision.DisabledCaption);
                        if (!state.ButtonDisabledByParsek)
                            ParsekLog.Info(Tag, "Astronaut Complex " + decision.BlockKind + " button disabled for "
                                + name + " tab=" + tab + " why=\"" + decision.DisabledCaption + "\"");
                        state.ButtonDisabledByParsek = true;
                        if (counts != null)
                        {
                            if (decision.BlockKind == "hire") counts.HireDisabled++;
                            else counts.DismissDisabled++;
                        }
                    }
                }
                else if (state.ButtonDisabledByParsek)
                {
                    // An applicant's lock is re-set by stock in UpdateCrewCounts (it may be the
                    // crew-limit lock), so only a row stock never re-locks is re-enabled here.
                    if (tab != StockUiDecorationQuery.AstronautApplicantsTab)
                        row.SetButtonEnabled(true);
                    state.ButtonDisabledByParsek = false;
                    if (counts != null) counts.Restored++;
                    ParsekLog.Info(Tag, "Astronaut Complex button block lifted for " + name + " tab=" + tab);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "ac-decorate-row-failed",
                    "Astronaut Complex row annotation failed for " + name + " (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            return d;
        }

        /// <summary>
        /// <c>TooltipController_CrewAC.SetTooltip</c> postfix: append the explanation to the
        /// stock crew tooltip and make sure it shows (stock leaves <c>showTooltip</c> false
        /// when experience and G-limits are both off). Skipped when stock's own reason block
        /// already carries it (the <c>SetButtonEnabled</c> caption).
        /// </summary>
        internal static void AppendCrewTooltip(TooltipController_CrewAC tooltip, ProtoCrewMember pcm)
        {
            if (tooltip == null || pcm == null || string.IsNullOrEmpty(pcm.name)) return;
            var d = StockUiLiveSnapshot.Current.Kerbal(pcm.name, null);
            if (!ShouldAnnotateCrewTooltip(d)) return;
            string next = AppendTooltip(tooltip.descriptionString, d.Title, d.Why);
            if (!string.Equals(next, tooltip.descriptionString, StringComparison.Ordinal))
            {
                tooltip.descriptionString = next;
                ParsekLog.VerboseRateLimited(Tag, "ac-tooltip-" + pcm.name,
                    "Astronaut Complex tooltip annotated for " + pcm.name + " kind=" + d.Kind);
            }
            tooltip.showTooltip = true;
        }

        private static CrewListItem RowAt(UIList list, int index)
        {
            try
            {
                UIListItem item = list.GetUilistItemAt(index);
                return item != null ? item.GetComponent<CrewListItem>() : null;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "ac-row-at-failed",
                    "Astronaut Complex row lookup failed (" + ex.GetType().Name + ")");
                return null;
            }
        }

        private static CrewListItem FindRowByName(UIList list, string name)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                CrewListItem row = RowAt(list, i);
                if (row != null && string.Equals(SafeName(row), name, StringComparison.Ordinal))
                    return row;
            }
            return null;
        }

        internal static string SafeName(CrewListItem row)
        {
            if (row == null) return null;
            try
            {
                ProtoCrewMember crew = row.GetCrewRef();
                if (crew != null && !string.IsNullOrEmpty(crew.name)) return crew.name;
                return row.GetName();
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "ac-row-name-failed",
                    "Astronaut Complex row name lookup failed (" + ex.GetType().Name + ")");
                return null;
            }
        }

        internal static void ResetForTesting()
        {
            rowStates = new ConditionalWeakTable<CrewListItem, RowState>();
        }
    }
}
