using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The greyed look of a stock button Parsek disables (first in-game census 2026-09-25:
    /// R&amp;D Research / purchase-all and Mission Control Accept / Decline / Cancel looked
    /// live while non-interactable). The pure bookkeeping must grey on a block, give back the
    /// exact original on an unblock or a move to an unblocked item, never save its own grey
    /// as the original, and never overwrite a colour stock rewrote meanwhile.
    /// </summary>
    public class StockUiGreyedButtonTests
    {
        private static readonly Color White = new Color(1f, 1f, 1f, 1f);
        private static readonly Color Label = new Color(0.9f, 0.8f, 0.2f, 1f);

        private static List<KeyValuePair<int, Color>> Graphics(params Color[] colors)
        {
            var list = new List<KeyValuePair<int, Color>>();
            for (int i = 0; i < colors.Length; i++) list.Add(new KeyValuePair<int, Color>(i + 1, colors[i]));
            return list;
        }

        private static List<KeyValuePair<int, Color>> Apply(List<KeyValuePair<int, Color>> current,
            List<KeyValuePair<int, Color>> writes)
        {
            var result = new List<KeyValuePair<int, Color>>(current);
            foreach (var w in writes)
                for (int i = 0; i < result.Count; i++)
                    if (result[i].Key == w.Key) result[i] = new KeyValuePair<int, Color>(w.Key, w.Value);
            return result;
        }

        private static void AssertSame(Color expected, Color actual)
        {
            Assert.True(StockUiGreyedButton.SameColor(expected, actual), "expected " + expected + " got " + actual);
        }

        [Fact]
        public void GreyOf_IsUnitysDefaultDisabledTint()
        {
            Color grey = StockUiGreyedButton.GreyOf(White);
            AssertSame(new Color(0.784f, 0.784f, 0.784f, 0.502f), grey);
            Assert.False(StockUiGreyedButton.SameColor(White, grey));
        }

        [Fact]
        public void Block_GreysEveryGraphic_ThenUnblock_RestoresTheExactOriginals()
        {
            var ledger = new GreyLedger<int>();
            var shown = Graphics(White, Label);

            var greyWrites = ledger.Sync(true, shown);
            Assert.Equal(2, greyWrites.Count);
            shown = Apply(shown, greyWrites);
            AssertSame(StockUiGreyedButton.GreyOf(White), shown[0].Value);
            AssertSame(StockUiGreyedButton.GreyOf(Label), shown[1].Value);

            var restore = ledger.Sync(false, shown);
            shown = Apply(shown, restore);
            Assert.Equal(2, restore.Count);
            Assert.Equal(White, shown[0].Value);
            Assert.Equal(Label, shown[1].Value);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void RepeatedBlock_DoesNotDoubleSave_OrDoubleGrey()
        {
            // Stock re-runs UpdatePanel / RefreshUIControls while the same item stays blocked.
            var ledger = new GreyLedger<int>();
            var shown = Apply(Graphics(White), ledger.Sync(true, Graphics(White)));

            for (int i = 0; i < 3; i++)
                Assert.Empty(ledger.Sync(true, shown));
            GreyRecord rec;
            Assert.True(ledger.TryGet(1, out rec));
            Assert.Equal(White, rec.Original);

            shown = Apply(shown, ledger.Sync(false, shown));
            Assert.Equal(White, shown[0].Value);
        }

        [Fact]
        public void MovingToAnUnblockedItem_GivesTheStockLookBack()
        {
            // Blocked contract selected, then an ordinary one: the second sync is "not greyed".
            var ledger = new GreyLedger<int>();
            var shown = Apply(Graphics(White, Label), ledger.Sync(true, Graphics(White, Label)));

            shown = Apply(shown, ledger.Sync(false, shown));

            Assert.Equal(White, shown[0].Value);
            Assert.Equal(Label, shown[1].Value);
            // A later unblocked selection writes nothing at all.
            Assert.Empty(ledger.Sync(false, shown));
        }

        [Fact]
        public void UnblockOnAButtonNeverGreyed_WritesNothing()
        {
            var ledger = new GreyLedger<int>();
            Assert.Empty(ledger.Sync(false, Graphics(White, Label)));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void StockRewriteWhileGreyed_IsNeverOverwritten()
        {
            var ledger = new GreyLedger<int>();
            var shown = Apply(Graphics(White), ledger.Sync(true, Graphics(White)));
            var stockColor = new Color(0.2f, 0.9f, 0.2f, 1f);
            shown = Graphics(stockColor);

            // Release: stock owns the colour now, so the saved white is not written back.
            Assert.Empty(ledger.Sync(false, shown));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void StockRewriteWhileGreyed_ThenReblock_SavesStocksNewColour()
        {
            var ledger = new GreyLedger<int>();
            ledger.Sync(true, Graphics(White));
            var stockColor = new Color(0.2f, 0.9f, 0.2f, 1f);

            var writes = ledger.Sync(true, Graphics(stockColor));

            Assert.Single(writes);
            AssertSame(StockUiGreyedButton.GreyOf(stockColor), writes[0].Value);
            var restore = ledger.Sync(false, Apply(Graphics(stockColor), writes));
            Assert.Equal(stockColor, restore[0].Value);
        }

        [Fact]
        public void AGraphicThatDisappears_IsDropped_WithoutAWrite()
        {
            var ledger = new GreyLedger<int>();
            ledger.Sync(true, Graphics(White, Label));

            var writes = ledger.Sync(false, new List<KeyValuePair<int, Color>> { new KeyValuePair<int, Color>(1, StockUiGreyedButton.GreyOf(White)) });

            Assert.Single(writes);
            Assert.Equal(1, writes[0].Key);
            Assert.Equal(0, ledger.Count);
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(3, true, false)]
        [InlineData(2, false, false)]
        [InlineData(2, true, true)]
        public void StockDrawsDisabled_SpriteSwapNeedsADistinctDisabledSprite(
            int transition, bool distinctSprite, bool expected)
        {
            Assert.Equal(expected, StockUiGreyedButton.StockDrawsDisabled((StockButtonTransition)transition, distinctSprite, White, White, 1f));
        }

        [Fact]
        public void StockDrawsDisabled_ColorTintNeedsAVisiblyDifferentDisabledColour()
        {
            var unityDefault = new Color(0.784f, 0.784f, 0.784f, 0.502f);
            Assert.True(StockUiGreyedButton.StockDrawsDisabled(StockButtonTransition.ColorTint, false, White, unityDefault, 1f));
            Assert.False(StockUiGreyedButton.StockDrawsDisabled(StockButtonTransition.ColorTint, false, White, White, 1f));
            Assert.False(StockUiGreyedButton.StockDrawsDisabled(StockButtonTransition.ColorTint, false, White,
                new Color(0.98f, 0.98f, 0.98f, 1f), 1f));
        }

        [Fact]
        public void MissionControl_GreysExactlyTheButtonsTheDecisionDisables()
        {
            bool a, d, c;
            MissionControlStockAnnotation.GreyedButtons(default(StockUiDecoration), out a, out d, out c);
            Assert.False(a || d || c);

            MissionControlStockAnnotation.GreyedButtons(new StockUiDecoration
                { Kind = StockUiDecorationKind.ContractAccept, Blocked = true, Marked = true }, out a, out d, out c);
            Assert.True(a && d);
            Assert.False(c);

            MissionControlStockAnnotation.GreyedButtons(new StockUiDecoration
                { Kind = StockUiDecorationKind.ContractSlot, Blocked = true }, out a, out d, out c);
            Assert.True(a);
            Assert.False(d || c);

            MissionControlStockAnnotation.GreyedButtons(new StockUiDecoration
                { Kind = StockUiDecorationKind.ContractResolution, Blocked = true, Marked = true }, out a, out d, out c);
            Assert.True(c);
            Assert.False(a || d);

            MissionControlStockAnnotation.GreyedButtons(new StockUiDecoration
                { Kind = StockUiDecorationKind.ContractAccept, Blocked = false, Marked = false }, out a, out d, out c);
            Assert.False(a || d || c);
        }
    }
}
