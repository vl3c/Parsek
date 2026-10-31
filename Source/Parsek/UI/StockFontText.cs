namespace Parsek
{
    /// <summary>
    /// Text Parsek hands to a STOCK uGUI surface (a <c>PopupDialog</c> body, a
    /// <c>ScreenMessages</c> toast). Those render through KSP's TextMeshPro font, which has
    /// no glyph for U+2192 (the arrow in a default route name, "Route: KSC → Mun"), so TMP
    /// draws its missing-glyph box there; IMGUI windows draw the arrow fine and keep it.
    /// Measured by the GUI-25 census (todo GUI-CENSUS-WAVE6-RESIDUE item 8).
    /// </summary>
    internal static class StockFontText
    {
        /// <summary>The text with every character the stock TMP font cannot draw replaced
        /// by an ASCII stand-in. Null stays null. Pure.</summary>
        internal static string ForStockUi(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('→') < 0) return text;
            return text.Replace("→", "->");
        }
    }
}
