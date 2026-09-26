using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// The transition a stock <c>Selectable</c> uses, mirrored so the pure decision below
    /// needs no UnityEngine.UI reference.
    /// </summary>
    internal enum StockButtonTransition
    {
        None,
        ColorTint,
        SpriteSwap,
        Animation
    }

    /// <summary>
    /// What Parsek wrote on one graphic of a greyed button: the colour stock had before the
    /// grey (restored when the block lifts) and the grey Parsek wrote (so a later stock write
    /// is recognised and never overwritten).
    /// </summary>
    internal struct GreyRecord
    {
        internal Color Original;
        internal Color Applied;
    }

    /// <summary>
    /// The per-button bookkeeping of the greyed state, over any key (a graphic's instance id
    /// live, an int in the tests). Pure: it never touches Unity, it only answers which colour
    /// to write.
    /// <list type="bullet">
    /// <item>Block, no record: save the current colour, write the grey.</item>
    /// <item>Block, record, current colour is still Parsek's grey: nothing (no double-save,
    /// so the grey is never saved as the "original").</item>
    /// <item>Block, record, stock rewrote the colour meanwhile: save stock's new colour,
    /// grey it.</item>
    /// <item>Release, current colour is still Parsek's grey: write the saved original.</item>
    /// <item>Release, stock rewrote the colour meanwhile: leave stock's colour.</item>
    /// </list>
    /// </summary>
    internal sealed class GreyLedger<TKey>
    {
        private readonly Dictionary<TKey, GreyRecord> records = new Dictionary<TKey, GreyRecord>();

        internal int Count => records.Count;

        internal bool Holds(TKey key) => records.ContainsKey(key);

        internal bool TryGet(TKey key, out GreyRecord record) => records.TryGetValue(key, out record);

        /// <summary>The colour to write to grey <paramref name="key"/>, or null to leave it.</summary>
        internal Color? Block(TKey key, Color current)
        {
            GreyRecord rec;
            if (records.TryGetValue(key, out rec) && StockUiGreyedButton.SameColor(current, rec.Applied))
                return null;
            var next = new GreyRecord { Original = current, Applied = StockUiGreyedButton.GreyOf(current) };
            records[key] = next;
            return next.Applied;
        }

        /// <summary>The colour to write to un-grey <paramref name="key"/>, or null to leave
        /// it. The record is dropped either way.</summary>
        internal Color? Release(TKey key, Color current)
        {
            GreyRecord rec;
            if (!records.TryGetValue(key, out rec)) return null;
            records.Remove(key);
            return StockUiGreyedButton.SameColor(current, rec.Applied) ? rec.Original : (Color?)null;
        }

        /// <summary>
        /// One pass over a button's graphics: grey every one (<paramref name="greyed"/>) or
        /// release every one Parsek greyed. Returns the writes to make. A graphic held from an
        /// earlier pass that is no longer listed is dropped without a write (it is gone).
        /// </summary>
        internal List<KeyValuePair<TKey, Color>> Sync(bool greyed, IList<KeyValuePair<TKey, Color>> current)
        {
            var writes = new List<KeyValuePair<TKey, Color>>();
            var listed = new HashSet<TKey>();
            if (current != null)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    listed.Add(current[i].Key);
                    Color? write = greyed
                        ? Block(current[i].Key, current[i].Value)
                        : Release(current[i].Key, current[i].Value);
                    if (write.HasValue)
                        writes.Add(new KeyValuePair<TKey, Color>(current[i].Key, write.Value));
                }
            }
            if (records.Count > 0)
            {
                var stale = new List<TKey>();
                foreach (var key in records.Keys)
                    if (!listed.Contains(key)) stale.Add(key);
                for (int i = 0; i < stale.Count; i++) records.Remove(stale[i]);
            }
            return writes;
        }
    }

    /// <summary>
    /// The greyed look of a stock button Parsek disables (owner ruling D1, "the control's
    /// disabled or greyed state"). Several stock buttons draw no disabled state at all: the
    /// R&amp;D side panel's <c>actionButton</c> (a <c>UIStateButton</c>, SpriteSwap from
    /// <c>ButtonState</c> sprites) and Mission Control's Accept / Decline / Cancel look the
    /// same with <c>interactable = false</c> (first in-game census, 2026-09-25), so the
    /// reason text was the only cue. When the button's own transition draws a disabled
    /// state (a distinct disabled sprite, or a ColorTint whose disabled colour differs),
    /// that stock visual is used and nothing is tinted; otherwise every graphic on the
    /// button (its image, icon and label) is multiplied by Unity's default disabled tint.
    /// <para>
    /// The originals are kept per graphic, per button instance, and restored the moment the
    /// block lifts or the selection moves to an unblocked item. Stock never writes
    /// <c>Graphic.color</c> on these buttons (<c>ButtonState.Setup</c> swaps sprites and is
    /// called without a label; <c>RDController</c> and <c>MissionControl</c> write text,
    /// <c>interactable</c> and <c>SetActive</c> only), and <see cref="GreyLedger{TKey}"/>
    /// still refuses to overwrite a colour stock rewrote while greyed, so a restore can
    /// never carry Parsek's grey onto the next item or clobber a stock colour.
    /// </para>
    /// </summary>
    internal static class StockUiGreyedButton
    {
        private const string Tag = "StockUiOverlay";

        /// <summary>Unity's default <c>ColorBlock.disabledColor</c>, used as a multiplier.</summary>
        internal static readonly Color DisabledTint = new Color(0.78431f, 0.78431f, 0.78431f, 0.50196f);

        private const float ColorTolerance = 0.004f;
        private const float DistinctTintThreshold = 0.1f;

        internal static Color GreyOf(Color c)
        {
            return new Color(c.r * DisabledTint.r, c.g * DisabledTint.g, c.b * DisabledTint.b, c.a * DisabledTint.a);
        }

        internal static bool SameColor(Color a, Color b)
        {
            return Math.Abs(a.r - b.r) <= ColorTolerance
                && Math.Abs(a.g - b.g) <= ColorTolerance
                && Math.Abs(a.b - b.b) <= ColorTolerance
                && Math.Abs(a.a - b.a) <= ColorTolerance;
        }

        /// <summary>
        /// True when the button's own transition already draws a visible disabled state, so
        /// stock's visual is used and Parsek tints nothing: SpriteSwap with a disabled sprite
        /// other than the normal one, or ColorTint whose disabled colour (times the colour
        /// multiplier) differs visibly from the normal one.
        /// </summary>
        internal static bool StockDrawsDisabled(
            StockButtonTransition transition, bool hasDistinctDisabledSprite,
            Color normalColor, Color disabledColor, float colorMultiplier)
        {
            switch (transition)
            {
                case StockButtonTransition.SpriteSwap:
                    return hasDistinctDisabledSprite;
                case StockButtonTransition.ColorTint:
                    float m = colorMultiplier <= 0f ? 1f : colorMultiplier;
                    return Math.Abs(normalColor.r * m - disabledColor.r * m) > DistinctTintThreshold
                        || Math.Abs(normalColor.g * m - disabledColor.g * m) > DistinctTintThreshold
                        || Math.Abs(normalColor.b * m - disabledColor.b * m) > DistinctTintThreshold
                        || Math.Abs(normalColor.a * m - disabledColor.a * m) > DistinctTintThreshold;
                default:
                    return false;
            }
        }

        // ---------------- live ----------------

        private sealed class ButtonEntry
        {
            internal Button Button;
            internal readonly GreyLedger<int> Ledger = new GreyLedger<int>();
            internal readonly Dictionary<int, Graphic> Graphics = new Dictionary<int, Graphic>();
        }

        private static readonly Dictionary<int, ButtonEntry> entries = new Dictionary<int, ButtonEntry>();
        private static readonly HashSet<int> stockVisualLogged = new HashSet<int>();

        /// <summary>The number of buttons Parsek currently holds greyed (tests, logs).</summary>
        internal static int GreyedButtonCount
        {
            get
            {
                int n = 0;
                foreach (var e in entries.Values) if (e.Ledger.Count > 0) n++;
                return n;
            }
        }

        private static StockButtonTransition MapTransition(Selectable.Transition t)
        {
            switch (t)
            {
                case Selectable.Transition.ColorTint: return StockButtonTransition.ColorTint;
                case Selectable.Transition.SpriteSwap: return StockButtonTransition.SpriteSwap;
                case Selectable.Transition.Animation: return StockButtonTransition.Animation;
                default: return StockButtonTransition.None;
            }
        }

        /// <summary>Whether this live button draws its own disabled state.</summary>
        internal static bool StockDrawsDisabled(Button button, out string describe)
        {
            var transition = MapTransition(button.transition);
            Sprite disabledSprite = button.spriteState.disabledSprite;
            var image = button.targetGraphic as Image;
            bool distinctSprite = disabledSprite != null && (image == null || image.sprite != disabledSprite);
            ColorBlock colors = button.colors;
            describe = "transition=" + transition
                + " disabledSprite=" + (disabledSprite != null ? disabledSprite.name : "none")
                + " disabledColor=" + FormatColor(colors.disabledColor)
                + " normalColor=" + FormatColor(colors.normalColor);
            return StockDrawsDisabled(transition, distinctSprite, colors.normalColor, colors.disabledColor, colors.colorMultiplier);
        }

        /// <summary>
        /// Greys or un-greys <paramref name="button"/>. Call after every stock write the
        /// block follows (the same postfixes that set <c>interactable</c>), with
        /// <paramref name="greyed"/> true exactly when PARSEK disabled the button for the
        /// item on show; false restores the originals Parsek saved (a no-op for a button
        /// Parsek never greyed). <paramref name="label"/> names the control in the log.
        /// </summary>
        internal static void Sync(Button button, bool greyed, string label)
        {
            PruneDestroyed();
            if (button == null) return;
            int id = button.GetInstanceID();
            ButtonEntry entry;
            entries.TryGetValue(id, out entry);

            if (greyed)
            {
                string describe;
                if (StockDrawsDisabled(button, out describe))
                {
                    if (stockVisualLogged.Add(id))
                        ParsekLog.Verbose(Tag, "Greyed state for " + (label ?? "?")
                            + ": stock draws its own disabled state (" + describe + ") - no tint");
                    greyed = false;
                    if (entry == null) return;
                }
                else if (entry == null)
                {
                    entry = new ButtonEntry { Button = button };
                    entries[id] = entry;
                    ParsekLog.Verbose(Tag, "Greyed state for " + (label ?? "?")
                        + ": stock draws no disabled state (" + describe + ") - tinting the button's graphics");
                }
            }
            else if (entry == null)
            {
                return;
            }

            Graphic[] graphics = button.GetComponentsInChildren<Graphic>(true);
            var current = new List<KeyValuePair<int, Color>>(graphics.Length);
            entry.Graphics.Clear();
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] == null) continue;
                int gid = graphics[i].GetInstanceID();
                entry.Graphics[gid] = graphics[i];
                current.Add(new KeyValuePair<int, Color>(gid, graphics[i].color));
            }
            var writes = entry.Ledger.Sync(greyed, current);
            for (int i = 0; i < writes.Count; i++)
            {
                Graphic g;
                if (entry.Graphics.TryGetValue(writes[i].Key, out g) && g != null)
                    g.color = writes[i].Value;
            }
            if (!greyed)
                entries.Remove(id);

            if (writes.Count > 0)
                ParsekLog.VerboseRateLimited(Tag, "greyed-" + (label ?? "?") + (greyed ? "-on" : "-off"),
                    (label ?? "?") + (greyed ? " greyed" : " greyed state lifted, stock colours restored")
                    + " (graphics=" + current.Count.ToString(CultureInfo.InvariantCulture)
                    + " written=" + writes.Count.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>True while Parsek holds <paramref name="button"/> greyed.</summary>
        internal static bool IsGreyedByParsek(Button button)
        {
            if (button == null) return false;
            ButtonEntry entry;
            return entries.TryGetValue(button.GetInstanceID(), out entry) && entry.Ledger.Count > 0;
        }

        private static void PruneDestroyed()
        {
            if (entries.Count == 0) return;
            List<int> dead = null;
            foreach (var kv in entries)
            {
                if (kv.Value.Button == null)
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) entries.Remove(dead[i]);
        }

        private static string FormatColor(Color c)
        {
            var ic = CultureInfo.InvariantCulture;
            return "(" + c.r.ToString("F2", ic) + "," + c.g.ToString("F2", ic) + ","
                + c.b.ToString("F2", ic) + "," + c.a.ToString("F2", ic) + ")";
        }

        internal static void ResetForTesting()
        {
            entries.Clear();
            stockVisualLogged.Clear();
        }
    }
}
