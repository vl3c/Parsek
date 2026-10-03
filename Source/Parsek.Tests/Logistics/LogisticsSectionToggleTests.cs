using System;
using System.Security;
using Parsek;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The Logistics section title bars' collapse state: per section, session-only, all
    /// expanded by default, flipped by the same path the title-bar click queues.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsSectionToggleTests
    {
        [Fact]
        public void SectionsStartExpandedAndToggleIndependently()
        {
            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                LogisticsWindowUI w = ui.GetLogisticsUI();
                string active = LogisticsRoutePresentation.ActiveSectionName;
                string paused = LogisticsRoutePresentation.PausedSectionName;
                string candidates = LogisticsRoutePresentation.CandidatesSectionName;

                Assert.False(w.IsSectionCollapsedForTesting(active));
                Assert.False(w.IsSectionCollapsedForTesting(paused));
                Assert.False(w.IsSectionCollapsedForTesting(candidates));

                w.ToggleSectionForTesting(paused);
                Assert.True(w.IsSectionCollapsedForTesting(paused));
                Assert.False(w.IsSectionCollapsedForTesting(active));
                Assert.False(w.IsSectionCollapsedForTesting(candidates));

                w.ToggleSectionForTesting(paused);
                Assert.False(w.IsSectionCollapsedForTesting(paused));

                w.ToggleSectionForTesting(null); // ignored
                Assert.False(w.IsSectionCollapsedForTesting(active));
            }
            finally
            {
                try { ui.Cleanup(); }
                catch (SecurityException) { }
                catch (MissingMethodException) { }
            }
        }
    }
}
