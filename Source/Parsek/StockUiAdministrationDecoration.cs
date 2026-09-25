using System;
using System.Runtime.CompilerServices;
using KSP.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>What the Administration Accept / Cancel look pass does to the button.</summary>
    internal enum AdministrationButtonLookAction
    {
        /// <summary>Leave the button as it is (stock look, or already greyed by Parsek).</summary>
        None,
        /// <summary>Capture the stock colour and grey the button.</summary>
        Grey,
        /// <summary>Put the captured stock colour back.</summary>
        RestoreStock
    }

    /// <summary>
    /// The Administration Accept / Cancel button's greyed look for a Parsek refusal
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, stock-control
    /// annotation rule D1: the control's greyed state, paired with the click-block).
    ///
    /// <para>Stock disables that button with <c>UIStateButton.Enable(false)</c>, which only
    /// sets <c>Button.interactable</c>; the "accept" / "cancel" <c>ButtonState</c> sprites
    /// give it no distinct disabled picture, so a disabled button looks exactly like an
    /// enabled one - for stock's own refusals too (the GUI-28 capture
    /// <c>stk-admin-strategy</c>: stock's slot check refused, <c>interactable=false</c>, the
    /// green check still bright). With no stock disabled visual to reuse, Parsek tints the
    /// button's own image with the button's own <c>ColorBlock.disabledColor</c> (the colour
    /// stock declares for this control's disabled state), or half alpha when that colour is
    /// neutral, and only while a PARSEK refusal is what disables it. A stock refusal keeps
    /// stock's look (the stock-first precedence leaves stock's control untouched).</para>
    ///
    /// <para>The stock colour is captured once, when the grey is applied, and restored
    /// exactly when the selection moves to a strategy Parsek does not refuse. Stock never
    /// writes the image colour (<c>ButtonState.Setup</c> swaps sprites only), so without the
    /// restore a grey would leak onto the next selection; a greyed colour is never captured
    /// as the stock one.</para>
    /// </summary>
    internal static class StockUiAdministrationDecoration
    {
        private const string Tag = "StrategyReservation";

        /// <summary>The disabled tint when the button's own disabled colour is neutral:
        /// half alpha, stock's crew-dialog <c>disabledColor</c>.</summary>
        internal static readonly Color FallbackDisabledTint = new Color(1f, 1f, 1f, 0.5f);

        internal sealed class ButtonLookState
        {
            internal bool Greyed;
            internal Color StockColor;
        }

        private static ConditionalWeakTable<UIStateButton, ButtonLookState> states =
            new ConditionalWeakTable<UIStateButton, ButtonLookState>();

        /// <summary>
        /// The look decision: grey only a button a Parsek refusal disabled (never a live one:
        /// a mark on a live control misleads), restore the stock look once it is not.
        /// </summary>
        internal static AdministrationButtonLookAction DecideLook(bool parsekBlocked, bool interactable, bool greyed)
        {
            bool wantGrey = parsekBlocked && !interactable;
            if (wantGrey) return greyed ? AdministrationButtonLookAction.None : AdministrationButtonLookAction.Grey;
            return greyed ? AdministrationButtonLookAction.RestoreStock : AdministrationButtonLookAction.None;
        }

        /// <summary>The greyed colour: the stock colour times the button's own disabled
        /// colour, or times <see cref="FallbackDisabledTint"/> when that one is neutral.</summary>
        internal static Color GreyedColor(Color stock, Color stockDisabledTint)
        {
            Color f = IsNeutral(stockDisabledTint) ? FallbackDisabledTint : stockDisabledTint;
            return new Color(stock.r * f.r, stock.g * f.g, stock.b * f.b, stock.a * f.a);
        }

        private static bool IsNeutral(Color c)
        {
            return c.r >= 0.99f && c.g >= 0.99f && c.b >= 0.99f && c.a >= 0.99f;
        }

        /// <summary>
        /// One step of the look state machine over the image colour: returns the colour to
        /// write, or null to leave the image alone. Pure over <paramref name="state"/>.
        /// </summary>
        internal static Color? Step(
            ButtonLookState state, bool parsekBlocked, bool interactable, Color current, Color stockDisabledTint,
            out AdministrationButtonLookAction action)
        {
            action = DecideLook(parsekBlocked, interactable, state.Greyed);
            switch (action)
            {
                case AdministrationButtonLookAction.Grey:
                    state.StockColor = current;
                    state.Greyed = true;
                    return GreyedColor(current, stockDisabledTint);
                case AdministrationButtonLookAction.RestoreStock:
                    state.Greyed = false;
                    return state.StockColor;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Re-derives the Accept / Cancel look after stock (and the Parsek postfixes) set the
        /// button for the current selection. <paramref name="buttonState"/> is <c>accept</c>
        /// or <c>cancel</c>, for the log.
        /// </summary>
        internal static void RefreshButtonLook(UIStateButton button, bool parsekBlocked, string strategyId, string buttonState)
        {
            if (button == null) return;
            try
            {
                Image image = button.Image;
                Button uiButton = button.Button;
                if (image == null || uiButton == null) return;
                ButtonLookState state = states.GetValue(button, _ => new ButtonLookState());
                AdministrationButtonLookAction action;
                Color? next = Step(state, parsekBlocked, uiButton.interactable, image.color,
                    uiButton.colors.disabledColor, out action);
                if (next.HasValue) image.color = next.Value;
                if (action == AdministrationButtonLookAction.Grey)
                    ParsekLog.Info(Tag, "Administration: " + buttonState + " button greyed (Parsek refusal) strategy="
                        + (strategyId ?? "<none>"));
                else if (action == AdministrationButtonLookAction.RestoreStock)
                    ParsekLog.Info(Tag, "Administration: " + buttonState + " button restored to its stock look strategy="
                        + (strategyId ?? "<none>"));
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "admin-button-look-failed",
                    "Administration button look failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>Whether Parsek has greyed this button (the census and the in-game cells
        /// read it next to <c>interactable</c>).</summary>
        internal static bool IsGreyedByParsek(UIStateButton button)
        {
            ButtonLookState state;
            return button != null && states.TryGetValue(button, out state) && state.Greyed;
        }

        internal static void ResetForTesting()
        {
            states = new ConditionalWeakTable<UIStateButton, ButtonLookState>();
        }
    }
}
