using System;
using System.Runtime.CompilerServices;
using KSP.UI.TooltipTypes;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The reason on a stock button Parsek greys (owner coverage rule, 2026-09-27: every
    /// disabled button says why). Stock mechanism only (ruling D1): the button's own stock
    /// <see cref="TooltipController_Text"/> gets the reason appended, or, when it has none,
    /// one is added with a prefab copied from a stock controller
    /// (<see cref="StockUiFacilityDecoration.FindTooltipPrefab"/>, the facility menu's
    /// precedent) and <c>RequireInteractable = false</c> so it draws over a disabled button.
    /// A lifted block gives back exactly what stock had: an added controller is emptied and
    /// disabled, a stock one gets its text, interactable rule and enabled state back.
    /// Per-button state lives in a weak table, so a destroyed button leaves nothing behind.
    /// </summary>
    internal static class StockUiReasonTooltip
    {
        private const string Tag = "StockUiOverlay";

        private sealed class State
        {
            internal TooltipController_Text Tip;
            internal bool Owned;
            internal string StockText;
            internal bool StockRequireInteractable;
            internal bool StockEnabled;
            internal string Why;
        }

        private static ConditionalWeakTable<Component, State> states = new ConditionalWeakTable<Component, State>();

        /// <summary>What the tooltip must read: the reason alone on an added controller,
        /// else stock's text with the reason appended on its own line. Pure.</summary>
        internal static string Compose(bool owned, string stockText, string why)
        {
            return StockUiFacilityDecoration.ComposeTooltipText(owned, stockText,
                StockUiFacilityDecoration.WrapTooltipText(why));
        }

        /// <summary>
        /// Show <paramref name="why"/> on <paramref name="control"/>'s tooltip when
        /// <paramref name="show"/>, else give back what stock had. Logs only a change
        /// (a new reason shown, or a reason taken away). Returns true when the reason is on
        /// the control.
        /// </summary>
        internal static bool Sync(Component control, bool show, string why, Component scope, string surface)
        {
            if (control == null) return false;
            try
            {
                State state;
                bool known = states.TryGetValue(control, out state);
                if (!show || string.IsNullOrEmpty(why))
                {
                    if (known && state.Why != null) Clear(state, surface);
                    return false;
                }
                if (!known) state = states.GetValue(control, _ => new State());
                if (state.Tip == null)
                {
                    var existing = control.GetComponent<TooltipController_Text>();
                    if (existing != null)
                    {
                        state.Tip = existing;
                        state.Owned = false;
                        state.StockText = existing.textString;
                        state.StockRequireInteractable = existing.RequireInteractable;
                        state.StockEnabled = existing.enabled;
                    }
                    else
                    {
                        var prefab = StockUiFacilityDecoration.FindTooltipPrefab(scope, surface);
                        if (prefab == null) return false;
                        var added = control.gameObject.AddComponent<TooltipController_Text>();
                        added.prefab = prefab;
                        state.Tip = added;
                        state.Owned = true;
                    }
                }
                var tip = state.Tip;
                if (tip.prefab == null)
                {
                    var prefab = StockUiFacilityDecoration.FindTooltipPrefab(scope, surface);
                    if (prefab == null) return false;
                    tip.prefab = prefab;
                }
                tip.RequireInteractable = false;
                tip.SetText(Compose(state.Owned, state.StockText, why));
                tip.enabled = true;
                if (!string.Equals(state.Why, why, StringComparison.Ordinal))
                    ParsekLog.Verbose(Tag, surface + ": reason shown on the disabled button's stock tooltip: " + why);
                state.Why = why;
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "reason-tooltip-failed|" + surface,
                    surface + ": reason tooltip failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                return false;
            }
        }

        private static void Clear(State state, string surface)
        {
            var tip = state.Tip;
            if (tip != null)
            {
                if (state.Owned)
                {
                    tip.SetText("");
                    tip.enabled = false;
                }
                else
                {
                    tip.SetText(state.StockText);
                    tip.RequireInteractable = state.StockRequireInteractable;
                    tip.enabled = state.StockEnabled;
                }
            }
            state.Why = null;
            ParsekLog.Verbose(Tag, surface + ": reason taken off the button's tooltip (block lifted), stock tooltip restored");
        }

        internal static void ResetForTesting()
        {
            states = new ConditionalWeakTable<Component, State>();
        }
    }
}
