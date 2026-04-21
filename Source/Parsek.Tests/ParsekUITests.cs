using UnityEngine;
using Xunit;

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
            Color normal = Color.black;
            Color hover = Color.black;
            Color focused = Color.black;
            Color active = Color.black;
            Color onNormal = Color.black;
            Color onHover = Color.black;
            Color onFocused = Color.black;
            Color onActive = Color.black;

            Color sourceNormal = new Color(0.92f, 0.92f, 0.92f, 1f);
            Color sourceOnNormal = new Color(0.82f, 0.82f, 0.82f, 1f);

            ParsekUI.NormalizeOpaqueWindowTitleTextColors(
                sourceNormal,
                sourceOnNormal,
                ref normal,
                ref hover,
                ref focused,
                ref active,
                ref onNormal,
                ref onHover,
                ref onFocused,
                ref onActive);

            Assert.Equal(sourceNormal, normal);
            Assert.Equal(sourceNormal, hover);
            Assert.Equal(sourceNormal, focused);
            Assert.Equal(sourceNormal, active);
            Assert.Equal(sourceOnNormal, onNormal);
            Assert.Equal(sourceOnNormal, onHover);
            Assert.Equal(sourceOnNormal, onFocused);
            Assert.Equal(sourceOnNormal, onActive);
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
