using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    /// <summary>
    /// Smoke-guard tests for ParsekUI wiring. Narrow scope: verifies that Phase 3
    /// wiring constructed the Career State window and exposes it via the accessor.
    /// Fails fast if ParsekUI forgets to `new` a sub-window or rename breaks the
    /// accessor — both invisible to CareerStateWindowUI-only tests.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekUITests
    {
        [Fact]
        public void ParsekUI_Ksc_Ctor_Exposes_CareerStateWindowUI()
        {
            // Regression: fails if Phase 3 wiring forgets to construct or expose
            // the Career State window. Mirrors how GetTimelineUI is the only
            // cross-window access path for the Kerbals Fates companion item.
            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                Assert.NotNull(ui.GetCareerStateUI());
            }
            finally
            {
                ui.Cleanup();
            }
        }

        [Fact]
        public void NormalizeOpaqueWindowTitleTextColors_ReplacesDarkFocusedStatesWithReadableBaseColor()
        {
            var source = new GUIStyle
            {
                normal = { textColor = new Color(0.92f, 0.92f, 0.92f, 1f) },
                onNormal = { textColor = new Color(0.82f, 0.82f, 0.82f, 1f) },
                focused = { textColor = Color.black },
                onFocused = { textColor = Color.black },
                active = { textColor = Color.black },
                onActive = { textColor = Color.black }
            };
            var style = new GUIStyle(source);

            ParsekUI.NormalizeOpaqueWindowTitleTextColors(style, source);

            Assert.Equal(source.normal.textColor, style.focused.textColor);
            Assert.Equal(source.normal.textColor, style.active.textColor);
            Assert.Equal(source.onNormal.textColor, style.onFocused.textColor);
            Assert.Equal(source.onNormal.textColor, style.onActive.textColor);
        }

        [Fact]
        public void ResolveReadableWindowTitleTextColor_FallsBackToWhiteWhenBothCandidatesAreDark()
        {
            Color resolved = ParsekUI.ResolveReadableWindowTitleTextColor(
                new Color(0.1f, 0.1f, 0.1f, 1f),
                new Color(0.2f, 0.2f, 0.2f, 1f));

            Assert.Equal(Color.white, resolved);
        }
    }
}
