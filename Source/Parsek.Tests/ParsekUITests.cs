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
    }
}
