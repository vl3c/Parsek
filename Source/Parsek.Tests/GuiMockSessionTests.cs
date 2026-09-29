using System;
using System.Collections.Generic;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The mock scope's lifecycle, and the property the whole feature's blast radius
    /// rests on: every suppression site is INERT when no session exists, so a player
    /// build behaves exactly as it did before this code shipped.
    /// </summary>
    [Collection("Sequential")]
    public class GuiMockSessionTests : IDisposable
    {
        private readonly bool savedSuppressLogging;
        private readonly List<string> logLines = new List<string>();

        public GuiMockSessionTests()
        {
            savedSuppressLogging = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GuiMockSession.ResetForTesting();
        }

        public void Dispose()
        {
            GuiMockSession.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = savedSuppressLogging;
        }

        [Fact]
        public void WithNoSessionEverySuppressionSiteIsInert()
        {
            // THE cell that makes "false in every player build" a fact rather than a
            // claim: nothing outside the armed seam can create a session, and with none
            // every declared site answers false without logging.
            Assert.False(GuiMockSession.IsLive);
            foreach (GuiMockSuppressionSite site in GuiMockSession.SuppressionSites)
            {
                Assert.False(GuiMockSession.Suppressed(site),
                    "site '" + site.Site + "' suppressed with NO session live");
                Assert.False(GuiMockSession.Owns(site.Window));
            }
            Assert.Empty(logLines);
        }

        [Fact]
        public void EverySuppressionSiteNamesASupportedWindowAndIsUniquelyKeyed()
        {
            var sites = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockSuppressionSite site in GuiMockSession.SuppressionSites)
            {
                Assert.True(sites.Add(site.Site),
                    "duplicate suppression site token '" + site.Site + "'; the one-shot "
                    + "Verbose line is keyed by it, so a duplicate silences one of them");
                Assert.True(GuiMockCatalogue.IsSupportedWindow(site.Window),
                    "site '" + site.Site + "' owns window '" + site.Window
                    + "', which the applier does not support");
            }
        }

        // A site owned by a window other than Kerbals. Only the Kerbals window declares
        // production sites, so the other-window case is a synthetic site on Structure.
        private static readonly GuiMockSuppressionSite OtherWindowSite =
            new GuiMockSuppressionSite { Site = "structure-probe", Window = GuiMockSession.StructureWindow };

        [Fact]
        public void ASessionSuppressesOnlyItsOwnWindowAndLogsOncePerSite()
        {
            bool restored = false;
            Assert.True(GuiMockSession.Begin("kerbals.roster.lost",
                GuiMockSession.KerbalsWindow, 100, "2026-09-22T00:00:00Z",
                () => { restored = true; }));

            Assert.True(GuiMockSession.Owns(GuiMockSession.KerbalsWindow));
            Assert.False(GuiMockSession.Owns(GuiMockSession.StructureWindow));
            Assert.False(GuiMockSession.Suppressed(OtherWindowSite),
                "a kerbals session must not suppress another window's site");

            Assert.True(GuiMockSession.Suppressed(GuiMockSession.KerbalsInvalidate));
            Assert.True(GuiMockSession.Suppressed(GuiMockSession.KerbalsInvalidate));
            Assert.True(GuiMockSession.Suppressed(GuiMockSession.KerbalsInvalidate));
            // ONE line for three calls: these sites run on a poll and on eight stock
            // GameEvents, so a per-call line would flood the log.
            Assert.Single(logLines,
                l => l.Contains("[GuiMock]") && l.Contains("mock suppression")
                     && l.Contains("site=kerbals-invalidate"));

            Assert.True(GuiMockSession.Clear("test", 7));
            Assert.True(restored);
            Assert.False(GuiMockSession.IsLive);
            Assert.False(GuiMockSession.Owns(GuiMockSession.KerbalsWindow));
            Assert.Contains(logLines,
                l => l.Contains("[GuiMock]") && l.Contains("mock restored")
                     && l.Contains("state=kerbals.roster.lost")
                     && l.Contains("heldFrames=7"));
        }

        [Fact]
        public void OnlyOneSessionLivesAtATime()
        {
            Assert.True(GuiMockSession.Begin("a.b.c", GuiMockSession.KerbalsWindow, 1, "t",
                                             () => { }));
            Assert.False(GuiMockSession.Begin("d.e.f", GuiMockSession.StructureWindow, 2, "t",
                                              () => { }),
                "a second Begin must refuse rather than shadow the live scope - two "
                + "windows' suppressions interacting is a state nobody can reason about");
            // And the refused Begin left the live scope exactly as it was.
            Assert.Equal("a.b.c", GuiMockSession.StateId);
            Assert.Equal(GuiMockSession.KerbalsWindow, GuiMockSession.Window);
        }

        [Fact]
        public void ClearWithNoSessionIsASilentNoOp()
        {
            // The teardown step of a lane runs after a state that may never have applied.
            Assert.False(GuiMockSession.Clear("teardown", -1));
            Assert.Empty(logLines);
        }

        [Fact]
        public void AThrowingRestoreDropsTheSessionAndReportsTheException()
        {
            Assert.True(GuiMockSession.Begin("a.b.c", GuiMockSession.StructureWindow, 5, "t",
                () => { throw new InvalidOperationException("boom"); }));

            Exception failure;
            Assert.False(GuiMockSession.Clear("test", 3, out failure));
            Assert.IsType<InvalidOperationException>(failure);
            // The session is GONE either way: the next apply starts from a clean state
            // rather than from a half-restored one.
            Assert.False(GuiMockSession.IsLive);
            Assert.Contains(logLines,
                l => l.Contains("[GuiMock]") && l.Contains("mock restore failed")
                     && l.Contains("exception=InvalidOperationException"));
        }

        [Fact]
        public void TheSuppressionNoteResetsBetweenSessions()
        {
            Assert.True(GuiMockSession.Begin("a.b.c", GuiMockSession.KerbalsWindow, 1, "t",
                                             () => { }));
            Assert.True(GuiMockSession.Suppressed(GuiMockSession.KerbalsInvalidate));
            Assert.True(GuiMockSession.Clear("test", 1));
            logLines.Clear();

            Assert.True(GuiMockSession.Begin("a.b.d", GuiMockSession.KerbalsWindow, 2, "t",
                                             () => { }));
            Assert.True(GuiMockSession.Suppressed(GuiMockSession.KerbalsInvalidate));
            Assert.Single(logLines,
                l => l.Contains("mock suppression") && l.Contains("site=kerbals-invalidate"));
        }

        [Fact]
        public void NoteScopeBrokenIsAOneShotAndIsInertWithoutAMatchingSession()
        {
            GuiMockSession.NoteScopeBroken(OtherWindowSite, "x");
            Assert.False(GuiMockSession.IsBroken);
            Assert.Empty(logLines);

            Assert.True(GuiMockSession.Begin("kerbals.roster.lost",
                GuiMockSession.KerbalsWindow, 1, "t", () => { }));
            // A site of ANOTHER window cannot break this scope.
            GuiMockSession.NoteScopeBroken(OtherWindowSite, "x");
            Assert.False(GuiMockSession.IsBroken);

            GuiMockSession.NoteScopeBroken(GuiMockSession.KerbalsInvalidate, "first");
            GuiMockSession.NoteScopeBroken(GuiMockSession.KerbalsInvalidate, "second");
            Assert.Equal("first", GuiMockSession.BrokenReason);
            Assert.Single(logLines, l => l.Contains("mock scope broken"));

            // And a fresh scope starts intact.
            Assert.True(GuiMockSession.Clear("test", 1));
            Assert.True(GuiMockSession.Begin("kerbals.roster.retired",
                GuiMockSession.KerbalsWindow, 2, "t", () => { }));
            Assert.False(GuiMockSession.IsBroken);
            Assert.Null(GuiMockSession.BrokenReason);
        }
    }
}
