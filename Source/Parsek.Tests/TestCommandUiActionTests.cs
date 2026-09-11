using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the GUI-census UI-driving verb (<see cref="TestCommandUiAction"/>):
    /// the op / window / tab / mode / rect vocabularies, the scene-availability rule, the
    /// asymmetric rect read-back, and the describe payload shape.
    ///
    /// <para>Three properties carry the most weight. First, a window the current scene does
    /// not draw must be a REJECTED rather than a cheerful OK: an "opened" Gloops recorder at
    /// the Space Center would produce a capture of the scene WITHOUT it, which a reviewer
    /// reads as a render defect. Second, the describe payload must be complete and stable -
    /// it is the inventory a supervisor reads instead of the source, so a row that
    /// disappears when a window is unavailable would silently shorten the census. Third, a
    /// read-back must be a statement about the GAME and not about the write: the settle rule
    /// (<see cref="TestCommandUiAction.DecideSettlePoll"/>) is what makes the rect tolerance
    /// predicate load-bearing at all, and what stops an <c>open</c> whose window
    /// force-closes itself on its first draw from reporting OK over empty scenery.</para>
    /// </summary>
    public class TestCommandUiActionTests
    {
        // ----- op parse -----

        [Fact]
        public void MissingOp_IsItsOwnReject()
        {
            Assert.False(TestCommandUiAction.TryParseOp(
                null, out UiActionOp op, out string reason));
            Assert.Equal(UiActionOp.None, op);
            Assert.Equal(TestCommandUiAction.OpArgMissingReason, reason);
        }

        // The op values are passed as INDEXES rather than as `UiActionOp` members: the enum
        // is internal (the whole pure half is), and an xUnit theory method must be public,
        // so an enum parameter would be a CS0051 accessibility error. Casting inside the
        // body keeps the parameter public-safe without widening the production enum.
        [Theory]
        [InlineData("open", 1)]
        [InlineData("close", 2)]
        [InlineData("tab", 3)]
        [InlineData("complexity", 4)]
        [InlineData("rect", 5)]
        [InlineData("describe", 6)]
        [InlineData("pointer", 7)]
        [InlineData("find", 8)]
        [InlineData("expand", 9)]
        [InlineData("target", 10)]
        [InlineData("picker", 11)]
        [InlineData("dialog", 12)]
        public void EveryOpToken_Parses_AndRoundTrips(string raw, int expectedOp)
        {
            Assert.True(TestCommandUiAction.TryParseOp(raw, out UiActionOp op, out string r));
            Assert.Equal((UiActionOp)expectedOp, op);
            Assert.Null(r);
            Assert.Equal(raw, TestCommandUiAction.OpToken(op));
        }

        [Fact]
        public void OpEnumValuesAreStable()
        {
            // The theory above pins tokens to numeric indexes, so the enum ordering is now
            // load-bearing for that cell. Pin it here too, or a re-order would silently
            // turn the round-trip theory into an assertion about the wrong op.
            Assert.Equal(0, (int)UiActionOp.None);
            Assert.Equal(1, (int)UiActionOp.Open);
            Assert.Equal(2, (int)UiActionOp.Close);
            Assert.Equal(3, (int)UiActionOp.Tab);
            Assert.Equal(4, (int)UiActionOp.Complexity);
            Assert.Equal(5, (int)UiActionOp.Rect);
            Assert.Equal(6, (int)UiActionOp.Describe);
            Assert.Equal(7, (int)UiActionOp.Pointer);
            Assert.Equal(8, (int)UiActionOp.Find);
            Assert.Equal(9, (int)UiActionOp.Expand);
            Assert.Equal(10, (int)UiActionOp.Target);
            Assert.Equal(11, (int)UiActionOp.Picker);
            Assert.Equal(12, (int)UiActionOp.Dialog);
        }

        [Theory]
        [InlineData("Open")]      // case-sensitive, the LoadGame scene= rule
        [InlineData("OPEN")]
        [InlineData("toggle")]
        [InlineData("")]
        public void UnknownOp_IsRejected(string raw)
        {
            Assert.False(TestCommandUiAction.TryParseOp(raw, out _, out string reason));
            Assert.Equal(TestCommandUiAction.OpArgInvalidReason, reason);
        }

        [Fact]
        public void ValidOpNames_ListsEveryOp_ForTheRejectMessage()
        {
            string listed = TestCommandUiAction.ValidOpNames;
            foreach (string token in new[] { "open", "close", "tab", "complexity", "rect",
                                            "describe", "pointer", "find", "expand",
                                            "target", "picker", "dialog" })
                Assert.Contains(token, listed.Split(','));
            Assert.Equal(12, listed.Split(',').Length);
        }

        [Theory]
        [InlineData(1, true)]    // open
        [InlineData(2, true)]    // close
        [InlineData(3, true)]    // tab
        [InlineData(5, true)]    // rect
        [InlineData(8, true)]    // find
        [InlineData(9, true)]    // expand
        [InlineData(10, true)]   // target
        [InlineData(11, true)]   // picker
        [InlineData(4, false)]   // complexity
        [InlineData(6, false)]   // describe
        [InlineData(7, false)]   // pointer - screen space, no window in the grammar
        [InlineData(12, false)]  // dialog - a uGUI popup no table row can name
        public void OpNeedsWindow_MatchesTheOpsThatNameOne(int op, bool needs)
        {
            Assert.Equal(needs, TestCommandUiAction.OpNeedsWindow((UiActionOp)op));
        }

        // ----- window table -----

        [Fact]
        public void TheTableIsExactlyTheDriveableWindows_InMainWindowOrder()
        {
            // A pinned list, not a count: the token set is what a census spec names in a
            // step AND in a capture label, so a rename or a re-order is a wire change and
            // must red here. The order is the main window's own button order so a describe
            // payload reads down the same list a reviewer sees on screen.
            Assert.Equal(
                new[] { "main", "missions", "timeline", "kerbals", "career", "logistics",
                        "structure", "settings", "spawncontrol", "gloops", "testrunner" },
                TestCommandUiAction.Windows.Select(w => w.Name).ToArray());
        }

        [Fact]
        public void EveryWindowNameIsUniqueAndLowerCase()
        {
            var names = TestCommandUiAction.Windows.Select(w => w.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (string name in names)
                Assert.Equal(name.ToLowerInvariant(), name);
        }

        [Fact]
        public void ValidWindowNames_ListsEveryWindow_ForTheRejectMessage()
        {
            // The reject message is the only place a spec author learns the spelling without
            // reading the source, so it must be the WHOLE list.
            Assert.Equal(TestCommandUiAction.Windows.Select(w => w.Name).ToArray(),
                TestCommandUiAction.ValidWindowNames.Split(','));
        }

        [Fact]
        public void MissingWindow_AndUnknownWindow_AreDistinctRejects()
        {
            Assert.False(TestCommandUiAction.TryResolveWindow(null, out _, out string missing));
            Assert.Equal(TestCommandUiAction.WindowArgMissingReason, missing);
            Assert.False(TestCommandUiAction.TryResolveWindow("nope", out _, out string unknown));
            Assert.Equal(TestCommandUiAction.WindowUnknownReason, unknown);
        }

        [Fact]
        public void KnownWindow_Resolves()
        {
            Assert.True(TestCommandUiAction.TryResolveWindow(
                "settings", out UiWindowSpec spec, out string reason));
            Assert.Equal("settings", spec.Name);
            Assert.Null(reason);
        }

        // ----- scene availability -----

        [Fact]
        public void ExactlySpawnControlAndGloopsAreFlightOnly()
        {
            // Derived from ParsekKSC.OnGUI, which draws every sub-window EXCEPT those two
            // (SpawnControlUI additionally self-closes out of flight). Pinned as a set so a
            // future window added to one host and not the other cannot slip through as an
            // "available" window that photographs empty.
            var flightOnly = TestCommandUiAction.Windows
                .Where(w => w.InFlight && !w.InSpaceCenter)
                .Select(w => w.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "gloops", "spawncontrol" }, flightOnly);

            // And nothing is KSC-only.
            Assert.Empty(TestCommandUiAction.Windows
                .Where(w => !w.InFlight && w.InSpaceCenter));
        }

        [Fact]
        public void OnlyFlightAndSpaceCenterHostAnyWindow()
        {
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
            {
                Assert.True(TestCommandUiAction.IsAvailableInScene(
                    spec, TestCommandScene.Flight) == spec.InFlight);
                Assert.True(TestCommandUiAction.IsAvailableInScene(
                    spec, TestCommandScene.SpaceCenter) == spec.InSpaceCenter);
                // Every other scene: nothing is available. ParsekTrackingStation.OnGUI draws
                // markers only, and no editor host exists at all.
                foreach (TestCommandScene scene in new[]
                         {
                             TestCommandScene.TrackingStation, TestCommandScene.Editor,
                             TestCommandScene.MainMenu, TestCommandScene.Loading,
                             TestCommandScene.Other,
                         })
                    Assert.False(TestCommandUiAction.IsAvailableInScene(spec, scene));
            }
        }

        // ----- tabs -----

        [Fact]
        public void TheTabbedWindowsAndTheirVocabulariesArePinned()
        {
            // One assertion per tabbed window rather than a count, for the same reason the
            // window list is pinned: a census names each tab in a step and in a label.
            Assert.Equal(new[] { "missions", "recordings" }, TabsOf("missions"));
            Assert.Equal(new[] { "overview", "details", "rewindff", "refly" },
                TabsOf("timeline"));
            Assert.Equal(new[] { "roster", "outcomes" }, TabsOf("kerbals"));
            Assert.Equal(new[] { "contracts", "strategies", "facilities", "milestones" },
                TabsOf("career"));
        }

        [Fact]
        public void TheUntabbedWindowsDeclareNoTabs()
        {
            // Settings' six sections all draw in ONE pass (three of them hidden in Basic, so
            // the Basic/Advanced capture pair is the section coverage), and Logistics'
            // section bubbles are expand-collapse rather than a selector. Neither is a tab
            // vocabulary, so `op=tab` on them is WindowHasNoTabs and not a silently ignored
            // arg.
            foreach (string name in new[] { "main", "logistics", "structure", "settings",
                                            "spawncontrol", "gloops", "testrunner" })
                Assert.Empty(TabsOf(name));
        }

        [Fact]
        public void TabOnAnUntabbedWindow_IsItsOwnReject()
        {
            // Distinct from tab-unknown: "this window has no tabs" and "this window has tabs
            // but not that one" send an author to different fixes.
            TestCommandUiAction.TryResolveWindow("settings", out UiWindowSpec settings, out _);
            Assert.False(TestCommandUiAction.TryResolveTab(
                settings, "looping", out _, out string reason));
            Assert.Equal(TestCommandUiAction.WindowHasNoTabsReason, reason);

            // Even with a null tab arg: the window-level fact wins, because supplying a tab
            // would not have helped.
            Assert.False(TestCommandUiAction.TryResolveTab(settings, null, out _, out reason));
            Assert.Equal(TestCommandUiAction.WindowHasNoTabsReason, reason);
        }

        [Fact]
        public void TabResolution_YieldsTheLiveFieldIndex()
        {
            TestCommandUiAction.TryResolveWindow("career", out UiWindowSpec career, out _);
            Assert.True(TestCommandUiAction.TryResolveTab(
                career, "facilities", out int index, out string reason));
            Assert.Equal(2, index);
            Assert.Null(reason);
            Assert.Equal("facilities", TestCommandUiAction.TabTokenAt(career, 2));
        }

        [Fact]
        public void MissingTabArg_OnATabbedWindow_IsTabArgMissing()
        {
            TestCommandUiAction.TryResolveWindow("kerbals", out UiWindowSpec kerbals, out _);
            Assert.False(TestCommandUiAction.TryResolveTab(kerbals, null, out _, out string r));
            Assert.Equal(TestCommandUiAction.TabArgMissingReason, r);
        }

        [Fact]
        public void UnknownTab_IsRejected_AndTheMessageListCarriesTheWindowsOwnTabs()
        {
            TestCommandUiAction.TryResolveWindow("kerbals", out UiWindowSpec kerbals, out _);
            Assert.False(TestCommandUiAction.TryResolveTab(
                kerbals, "contracts", out _, out string r));
            Assert.Equal(TestCommandUiAction.TabUnknownReason, r);
            Assert.Equal("roster,outcomes", TestCommandUiAction.TabNamesOf(kerbals));
        }

        [Fact]
        public void TabTokenAt_OutOfRange_IsAbsentRatherThanGuessed()
        {
            // A live field holding something the table does not model is reported as absent.
            // Guessing a token would put a tab name on the wire that was never selected.
            TestCommandUiAction.TryResolveWindow("kerbals", out UiWindowSpec kerbals, out _);
            Assert.Null(TestCommandUiAction.TabTokenAt(kerbals, -1));
            Assert.Null(TestCommandUiAction.TabTokenAt(kerbals, 2));
        }

        // ----- mode -----

        [Theory]
        [InlineData("basic", true)]
        [InlineData("advanced", false)]
        public void ModeTokens_Parse_AndRoundTrip(string raw, bool basic)
        {
            Assert.True(TestCommandUiAction.TryParseMode(raw, out bool got, out string r));
            Assert.Equal(basic, got);
            Assert.Null(r);
            Assert.Equal(raw, TestCommandUiAction.ModeToken(basic));
        }

        [Fact]
        public void ModeTokens_MatchTheProductionEnumSpelling()
        {
            // The wire tokens are the lower-cased UiComplexityMode member names, so a future
            // third mode cannot be added to the enum and silently miss the seam.
            Assert.Equal(TestCommandUiAction.BasicModeToken,
                UiComplexityMode.Basic.ToString().ToLowerInvariant());
            Assert.Equal(TestCommandUiAction.AdvancedModeToken,
                UiComplexityMode.Advanced.ToString().ToLowerInvariant());
            Assert.Equal(2, System.Enum.GetValues(typeof(UiComplexityMode)).Length);
        }

        [Theory]
        [InlineData("Basic")]
        [InlineData("simple")]
        [InlineData("")]
        public void UnknownMode_IsRejected(string raw)
        {
            Assert.False(TestCommandUiAction.TryParseMode(raw, out _, out string r));
            Assert.Equal(TestCommandUiAction.ModeArgInvalidReason, r);
        }

        [Fact]
        public void MissingMode_IsItsOwnReject()
        {
            Assert.False(TestCommandUiAction.TryParseMode(null, out _, out string r));
            Assert.Equal(TestCommandUiAction.ModeArgMissingReason, r);
        }

        // ----- rect parse -----

        [Fact]
        public void AllFourRectArgsAreRequired()
        {
            // A partial rect mixes a commanded position with a stale size, so the capture it
            // produces is not reproducible.
            Assert.False(TestCommandUiAction.TryParseRect(
                null, "0", "400", "300", out _, out string r1));
            Assert.Equal(TestCommandUiAction.RectArgMissingReason, r1);
            Assert.False(TestCommandUiAction.TryParseRect(
                "0", "0", "400", null, out _, out string r2));
            Assert.Equal(TestCommandUiAction.RectArgMissingReason, r2);
        }

        [Fact]
        public void AValidRectParses_Invariantly()
        {
            Assert.True(TestCommandUiAction.TryParseRect(
                "20", "40", "1200", "640", out UiActionRect rect, out string r));
            Assert.Null(r);
            Assert.Equal(20f, rect.X);
            Assert.Equal(40f, rect.Y);
            Assert.Equal(1200f, rect.W);
            Assert.Equal(640f, rect.H);
        }

        [Fact]
        public void RectParse_IsCultureInvariant_InBothDirections()
        {
            // A ro-RO / de-DE host prints "20,5" for 20.5 and parses "20.5" as 205 under its
            // own culture. The wire is dot-decimal by contract, so the parse must pin
            // InvariantCulture at the production site.
            using (new CultureSwap("de-DE"))
            {
                Assert.True(TestCommandUiAction.TryParseRect(
                    "20.5", "40.25", "1200", "640", out UiActionRect rect, out _));
                Assert.Equal(20.5f, rect.X);
                Assert.Equal(40.25f, rect.Y);
                // And the formatter does not emit a comma decimal.
                Assert.Equal("20,40,1200,640", TestCommandUiAction.FormatRect(
                    new UiActionRect { X = 20f, Y = 40f, W = 1200f, H = 640f }));
            }
        }

        [Fact]
        public void NegativeOriginsAreAllowed_ButBounded()
        {
            // Parking a window deliberately off-left is legitimate; an unbounded coordinate
            // is not.
            Assert.True(TestCommandUiAction.TryParseRect(
                "-200", "-50", "400", "300", out _, out _));
            Assert.False(TestCommandUiAction.TryParseRect(
                "-99999", "0", "400", "300", out _, out string r));
            Assert.Equal(TestCommandUiAction.RectArgInvalidReason, r);
        }

        [Theory]
        [InlineData("0", "0", "0", "300")]        // zero width draws nothing
        [InlineData("0", "0", "10", "300")]       // under the width floor
        [InlineData("0", "0", "400", "10")]       // under the height floor
        [InlineData("0", "0", "-400", "300")]     // negative size
        [InlineData("0", "0", "99999", "300")]    // over the size ceiling
        [InlineData("0", "0", "NaN", "300")]
        [InlineData("0", "0", "Infinity", "300")]
        [InlineData("x", "0", "400", "300")]      // unparseable
        [InlineData("0", "0", "4e9", "300")]
        public void OutOfRangeOrUnparseableRect_IsRejected(string x, string y,
                                                           string w, string h)
        {
            // A zero-width window photographs as a MISSING window, which is exactly the
            // false reading a GUI census must not produce.
            Assert.False(TestCommandUiAction.TryParseRect(x, y, w, h, out _, out string r));
            Assert.Equal(TestCommandUiAction.RectArgInvalidReason, r);
        }

        // ----- rect read-back -----

        [Theory]
        // THE asymmetry, and the reason the predicate is a named function. Every window
        // here is a GUILayout window that resolves to Max(passed, contentMin) on EACH axis,
        // so a window whose content does not fit the commanded box GROWS - that is the rect
        // being applied, not a failure. Strict equality would ERROR on a correct apply.
        [InlineData(800f, 400f, 800f, 940f, true)]   // height grew from content
        [InlineData(800f, 400f, 940f, 400f, true)]   // width grew from content
        [InlineData(800f, 400f, 940f, 940f, true)]   // both grew in one pass
        // THE MEASURED CASE, and the one that bought this theory. GUI-1-census-ksc's first
        // flight (`2026-09-10_2255`, step index 74) commanded the Settings window
        // `270,8,360,700` and read back `270,8,375,718` - it grew 15 px WIDE and 18 px tall
        // in the same layout pass. Under the old two-sided width check that was
        // `ERROR rect-not-applied` and the lane read driver-INVALID over a window that had
        // been placed exactly as asked.
        [InlineData(360f, 700f, 375f, 718f, true)]
        // The mirror direction, checked because a fix derived from an asymmetry has to be
        // walked both ways: each axis is a FLOOR, so shrinking below either is still a
        // failure. The width row is what keeps the settle honest - an UNRESOLVED rect reads
        // back the commanded value exactly, so the floor is the only thing left that can
        // catch a window which came back smaller than it was asked for.
        [InlineData(800f, 400f, 800f, 120f, false)]  // height below the floor
        [InlineData(800f, 400f, 250f, 400f, false)]  // width below the floor
        [InlineData(800f, 400f, 250f, 120f, false)]  // both below
        // The tolerance is slack ON the floor, not a two-sided band: one pixel under is
        // accepted on either axis, two is not.
        [InlineData(800f, 400f, 799f, 399f, true)]
        [InlineData(800f, 400f, 798f, 400f, false)]
        [InlineData(800f, 400f, 800f, 398f, false)]
        public void RectReadBack_TreatsBothSizeAxesAsFloors(
            float wantW, float wantH, float observedW, float observedH, bool applied)
        {
            var want = new UiActionRect { X = 10f, Y = 20f, W = wantW, H = wantH };
            var observed = new UiActionRect
                { X = 10f, Y = 20f, W = observedW, H = observedH };
            Assert.Equal(applied, TestCommandUiAction.RectAppliedWithinTolerance(
                want, observed, sizeIsHostControlled: false));
        }

        [Fact]
        public void RectReadBack_RejectsAMovedPosition()
        {
            // POSITION is the half that still holds EXACTLY: a window GUILayout grew is
            // still at the origin it was given, so both position axes stay two-sided.
            var want = new UiActionRect { X = 10f, Y = 20f, W = 800f, H = 400f };
            Assert.False(TestCommandUiAction.RectAppliedWithinTolerance(
                want, new UiActionRect { X = 90f, Y = 20f, W = 800f, H = 400f }, false));
            Assert.False(TestCommandUiAction.RectAppliedWithinTolerance(
                want, new UiActionRect { X = 10f, Y = 99f, W = 800f, H = 400f }, false));
            // Including the direction a FLOOR would have let through, which is the mistake
            // a copy-paste from the size axes would make.
            Assert.False(TestCommandUiAction.RectAppliedWithinTolerance(
                want, new UiActionRect { X = 4f, Y = 20f, W = 800f, H = 400f }, false));
            Assert.False(TestCommandUiAction.RectAppliedWithinTolerance(
                want, new UiActionRect { X = 10f, Y = 4f, W = 800f, H = 400f }, false));
        }

        [Fact]
        public void HostControlledSize_ChecksPositionOnly()
        {
            // BOTH of the main window's hosts pass a fixed GUILayout.Width(250) and BOTH
            // zero its height every frame (ParsekFlight.OnGUI and ParsekKSC.OnGUI each open
            // with `windowRect.height = 0f`), so only its POSITION can be commanded.
            // Checking its size would ERROR on every single rect op against it.
            Assert.True(TestCommandUiAction.SizeIsHostControlled("main"));
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows.Skip(1))
                Assert.False(TestCommandUiAction.SizeIsHostControlled(spec.Name));

            var want = new UiActionRect { X = 10f, Y = 20f, W = 900f, H = 700f };
            var host = new UiActionRect { X = 10f, Y = 20f, W = 250f, H = 0f };
            Assert.True(TestCommandUiAction.RectAppliedWithinTolerance(
                want, host, sizeIsHostControlled: true));
            Assert.False(TestCommandUiAction.RectAppliedWithinTolerance(
                new UiActionRect { X = 11.5f, Y = 20f, W = 900f, H = 700f }, host, true));
        }

        // ----- the two-phase ops -----

        [Theory]
        [InlineData(1, true)]     // open
        [InlineData(5, true)]     // rect
        [InlineData(2, false)]    // close
        [InlineData(3, false)]    // tab
        [InlineData(4, false)]    // complexity
        [InlineData(6, false)]    // describe
        [InlineData(7, true)]     // pointer
        [InlineData(8, true)]     // find
        [InlineData(9, true)]     // expand
        [InlineData(10, true)]    // target
        [InlineData(11, true)]    // picker
        [InlineData(12, false)]   // dialog - read-only, it writes nothing
        public void OpIsTwoPhase_IsEveryOpThatChangesDrawnState(int op, bool twoPhase)
        {
            // The set is pinned rather than counted, because each membership is its own
            // argument. `open` and `rect` are in it because their read-back only means
            // something AFTER a frame has been drawn - a window can force-close itself on
            // its first draw (SpawnControlUI with zero candidates in range), and a
            // GUILayout window's rect is resolved during the draw, so before a frame both
            // read back exactly the value just written and prove nothing.
            //
            // `close` is OUT after checking the MIRROR DIRECTION rather than by symmetry:
            // a drawing window can LOWER its own flag UNPROMPTED, while nothing raises one
            // WITHOUT A PLAYER CLICK. Draw paths do raise open flags (ParsekUI.cs:809's
            // RouteRunPrompt banner, RecordingsTableUI.cs:467/:521,
            // StructureListWindowUI.cs:89/:100) but all of those are GUILayout.Button
            // handlers, and the seam synthesises no clicks - so there is no self-opening
            // window a settled close read-back could catch. `tab` is out because the one live clamp
            // (RecordingsTableUI's Basic tab clamp) runs from the complexity LATCH, which
            // the applier drives synchronously in Update, not from a draw.
            //
            // The six GUI-census ops follow the same rule rather than an exception to it:
            // `pointer` / `find` / `expand` / `target` / `picker` all CHANGE what the next
            // frame draws (a hover, a capture, a disclosure, a window's contents, a popup),
            // so each holds the head for one frame and reads its own effect back. Only
            // `dialog` is single-phase, and only because it writes nothing at all. What the
            // settle CHECKS still differs per op, which is SettleChecksHostShowUi's job.
            Assert.Equal(twoPhase, TestCommandUiAction.OpIsTwoPhase((UiActionOp)op));
        }

        [Fact]
        public void FirstSettlePoll_BeforeAFrameIsDrawn_IsNeverSettled()
        {
            // THE headline property: zero elapsed frames cannot settle, which is what makes
            // the read-back a statement about the game rather than about the write.
            Assert.Equal(UiActionSettleOutcome.NotYet,
                TestCommandUiAction.DecideSettlePoll(framesElapsed: 0, budgetExpired: false));
            Assert.Equal(UiActionSettleOutcome.Settled,
                TestCommandUiAction.DecideSettlePoll(framesElapsed: 1, budgetExpired: false));
            Assert.Equal(1, TestCommandUiAction.SettleFrames);
        }

        [Fact]
        public void SettlePoll_TimesOutOnlyBeforeAFrame_AndSettledWinsOverTheBudget()
        {
            // ORDER MATTERS, the CaptureScreenshot.DecidePoll rule: a frame that landed on
            // the very poll the budget expired is a success, not an ERROR over state that
            // is already correct.
            Assert.Equal(UiActionSettleOutcome.TimedOut,
                TestCommandUiAction.DecideSettlePoll(framesElapsed: 0, budgetExpired: true));
            Assert.Equal(UiActionSettleOutcome.Settled,
                TestCommandUiAction.DecideSettlePoll(framesElapsed: 1, budgetExpired: true));
            // A negative elapsed count (Time.frameCount wrapping, or a re-armed field read
            // out of order) must not settle either - fail-closed on nonsense input.
            Assert.Equal(UiActionSettleOutcome.NotYet,
                TestCommandUiAction.DecideSettlePoll(framesElapsed: -3, budgetExpired: false));
        }

        // ----- the settle's host-visibility gate -----

        [Fact]
        public void SettleIsRefused_ForASubWindow_WhileTheHostSurfaceIsHidden()
        {
            // The frame count says a frame HAPPENED, never that this window was in it: both
            // hosts gate the WHOLE Parsek surface behind their own showUI (ParsekKSC.OnGUI's
            // `if (!showUI) return;`, ParsekFlight.OnGUI's `if (showUI)` block), and every
            // sub-window draw sits inside it. So a settled read-back over a hidden host is
            // the write reading itself back - and for `rect` it PASSES the tolerance check,
            // because an unresolved rect still holds exactly the commanded value, which is
            // how a census ends up photographing a scene with no Parsek window in it.
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows.Skip(1))
                Assert.True(TestCommandUiAction.SettleRefusedForHiddenHost(
                    spec.Name, hostShowUi: false), spec.Name);
        }

        [Fact]
        public void SettleIsAllowed_ForEveryWindow_OnceTheHostSurfaceIsShown()
        {
            // The mirror direction: the gate must not refuse the normal case. With `main`
            // open every window - including main itself - settles as before, so this is a
            // no-op on any census that opens main first (both committed ones do, as their
            // first UiAction step).
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
                Assert.False(TestCommandUiAction.SettleRefusedForHiddenHost(
                    spec.Name, hostShowUi: true), spec.Name);
        }

        [Fact]
        public void SettleIsNeverRefused_ForMainItself()
        {
            // MUST be exempt, and not as a convenience: `main`'s open flag IS the host's
            // showUI, so gating it on showUI would refuse `op=open window=main` - the first
            // step of every census and the op that turns the surface on in the first place.
            Assert.False(TestCommandUiAction.SettleRefusedForHiddenHost(
                TestCommandUiAction.MainWindow, hostShowUi: false));
            Assert.Equal("main", TestCommandUiAction.MainWindow);
            // Case-sensitive on purpose: the token reaching the settle came from
            // TryResolveWindow, which resolves to the table's own spelling.
            Assert.True(TestCommandUiAction.SettleRefusedForHiddenHost(
                "Main", hostShowUi: false));
        }

        [Fact]
        public void SettleIsRefused_ForAnUnrecognisedToken_WithTheHostHidden()
        {
            // Unreachable through the verb (TryResolveWindow rejects first), asserted so the
            // predicate is total and fails CLOSED: the safe answer for a token nobody
            // recognises is to refuse the read-back, never to trust it.
            Assert.True(TestCommandUiAction.SettleRefusedForHiddenHost(
                "not-a-window", hostShowUi: false));
            Assert.True(TestCommandUiAction.SettleRefusedForHiddenHost(
                null, hostShowUi: false));
            Assert.False(TestCommandUiAction.SettleRefusedForHiddenHost(
                null, hostShowUi: true));
        }

        // ----- the complexity no-op predicate -----

        [Fact]
        public void ComplexityAlreadySatisfied_RequiresBothTheLatchAndTheSetting()
        {
            // THE defect this closes: ParsekUI.SetUiComplexityMode no-ops when the request
            // equals the PERSISTED setting, so a save that persisted Basic under a fail-open
            // Advanced latch used to look "not already satisfied" on a Basic request, call a
            // setter that queued nothing, and then terminate complexity-not-applied on the
            // mode the save actually carried.
            Assert.True(TestCommandUiAction.IsComplexityAlreadySatisfied(
                UiComplexityMode.Basic, UiComplexityMode.Basic, UiComplexityMode.Basic));
            // Latch agrees, setting does not: NOT already - the setter still has to persist.
            Assert.False(TestCommandUiAction.IsComplexityAlreadySatisfied(
                UiComplexityMode.Basic, UiComplexityMode.Advanced, UiComplexityMode.Basic));
            // Setting agrees, latch does not (the drifted case): NOT already - the latch
            // still has to be driven, which is what TryRequeuePersistedUiComplexityMode is
            // for.
            Assert.False(TestCommandUiAction.IsComplexityAlreadySatisfied(
                UiComplexityMode.Advanced, UiComplexityMode.Basic, UiComplexityMode.Basic));
            Assert.False(TestCommandUiAction.IsComplexityAlreadySatisfied(
                UiComplexityMode.Advanced, UiComplexityMode.Advanced, UiComplexityMode.Basic));
        }

        // ----- the describe log line's open-window list -----

        [Fact]
        public void OpenWindowList_IsTableOrdered_AndDashWhenNoneIsOpen()
        {
            // It exists for the LOG line: the per-window w<i>open= keys are on the wire,
            // which never reaches KSP.log, so a census spec's logContracts could not
            // otherwise assert which windows were open. Table order makes the string
            // deterministic across runs, which is what lets a spec pin it as a literal.
            Assert.Equal("-", TestCommandUiAction.FormatOpenWindowList(
                new List<UiWindowState>()));
            Assert.Equal("-", TestCommandUiAction.FormatOpenWindowList(null));
            var rows = new List<UiWindowState>
            {
                new UiWindowState { Name = "main", Open = true },
                new UiWindowState { Name = "missions", Open = false },
                new UiWindowState { Name = "timeline", Open = true },
            };
            Assert.Equal("main,timeline", TestCommandUiAction.FormatOpenWindowList(rows));
        }

        // ----- payloads -----

        [Fact]
        public void TogglePayload_AlwaysCarriesAlready_BothWays()
        {
            var opened = TestCommandUiAction.BuildTogglePayload(
                UiActionOp.Open, "settings", openAfter: true, already: false);
            Assert.Equal(new[] { "op", "window", "open", "already" },
                opened.Select(kv => kv.Key).ToArray());
            Assert.Equal("open", Value(opened, "op"));
            Assert.Equal("settings", Value(opened, "window"));
            Assert.Equal("true", Value(opened, "open"));
            Assert.Equal("false", Value(opened, "already"));

            var closed = TestCommandUiAction.BuildTogglePayload(
                UiActionOp.Close, "settings", openAfter: false, already: true);
            Assert.Equal("close", Value(closed, "op"));
            Assert.Equal("false", Value(closed, "open"));
            Assert.Equal("true", Value(closed, "already"));
        }

        [Fact]
        public void TabPayload_CarriesBothTheTokenAndTheIndex()
        {
            var p = TestCommandUiAction.BuildTabPayload("career", "milestones", 3, false);
            Assert.Equal(new[] { "op", "window", "tab", "index", "already" },
                p.Select(kv => kv.Key).ToArray());
            Assert.Equal("milestones", Value(p, "tab"));
            Assert.Equal("3", Value(p, "index"));
        }

        [Fact]
        public void ComplexityPayload_EchoesTheMode()
        {
            var p = TestCommandUiAction.BuildComplexityPayload(basic: true, already: false);
            Assert.Equal(new[] { "op", "mode", "already" }, p.Select(kv => kv.Key).ToArray());
            Assert.Equal("basic", Value(p, "mode"));
        }

        [Fact]
        public void RectPayload_ReportsTheReadBackNotTheRequest()
        {
            var p = TestCommandUiAction.BuildRectPayload(
                "missions", new UiActionRect { X = 5f, Y = 6f, W = 700f, H = 812f },
                clamped: false, minW: 0f, minH: 0f);
            Assert.Equal("5,6,700,812", Value(p, "rect"));
            // Present BOTH ways (the `already` rule), so a reader of an unclamped call
            // knows the commanded size is what the window got.
            Assert.Equal("false", Value(p, "clamped"));
            Assert.Equal("0", Value(p, "minW"));
            Assert.Equal("0", Value(p, "minH"));
        }

        [Fact]
        public void DescribePayload_CarriesEveryWindow_WithAllSixKeys()
        {
            // THE inventory property. A row must exist for every window in the table,
            // including the ones this scene does not draw: a reader comparing a KSC describe
            // against a FLIGHT one needs the flight-only rows present-and-unavailable, and an
            // absent row is indistinguishable from an older seam build.
            var rows = TestCommandUiAction.Windows.Select(w => new UiWindowState
            {
                Name = w.Name,
                Available = w.InSpaceCenter,
                Open = false,
                RectKnown = false,
            }).ToList();
            var p = TestCommandUiAction.BuildDescribePayload("SPACECENTER", false, rows);

            Assert.Equal("describe", Value(p, "op"));
            Assert.Equal("SPACECENTER", Value(p, "scene"));
            Assert.Equal("advanced", Value(p, "complexity"));
            Assert.Equal(rows.Count.ToString(CultureInfo.InvariantCulture), Value(p, "count"));
            for (int i = 0; i < rows.Count; i++)
            {
                string prefix = "w" + i.ToString(CultureInfo.InvariantCulture);
                Assert.Equal(rows[i].Name, Value(p, prefix));
                foreach (string suffix in new[] { "avail", "open", "rect", "min", "tabs",
                                                  "tab" })
                    Assert.Contains(prefix + suffix, p.Select(kv => kv.Key));
            }
            // The two flight-only rows are present and marked unavailable.
            int gloops = rows.FindIndex(r => r.Name == "gloops");
            Assert.Equal("false", Value(p, "w" + gloops + "avail"));
        }

        [Fact]
        public void DescribePayload_UsesADashSentinelForAbsentValues()
        {
            // A single-character sentinel rather than an empty value: a trailing `key=` on
            // the wire is easy to misread as a truncated line.
            var rows = new List<UiWindowState>
            {
                new UiWindowState { Name = "settings", Available = true, Open = true,
                                    RectKnown = false, Tab = null },
            };
            var p = TestCommandUiAction.BuildDescribePayload("FLIGHT", false, rows);
            Assert.Equal("-", Value(p, "w0rect"));
            Assert.Equal("-", Value(p, "w0tabs"));
            Assert.Equal("-", Value(p, "w0tab"));
            // A window with no resize handle has no minimum at all, which `0,0` would
            // misreport as a real floor of zero.
            Assert.Equal("-", Value(p, "w0min"));
        }

        [Fact]
        public void DescribePayload_ReportsAKnownRectAndASelectedTab()
        {
            var rows = new List<UiWindowState>
            {
                new UiWindowState
                {
                    Name = "career", Available = true, Open = true, RectKnown = true,
                    Rect = new UiActionRect { X = 280f, Y = 100f, W = 980f, H = 560f },
                    Tab = "strategies",
                },
            };
            var p = TestCommandUiAction.BuildDescribePayload("FLIGHT", true, rows);
            Assert.Equal("280,100,980,560", Value(p, "w0rect"));
            Assert.Equal("contracts,strategies,facilities,milestones", Value(p, "w0tabs"));
            Assert.Equal("strategies", Value(p, "w0tab"));
            Assert.Equal("basic", Value(p, "complexity"));
        }

        [Fact]
        public void DescribePayload_OnAnEmptyRowList_IsStillAWellFormedHeader()
        {
            // Safe on the degenerate input, like every other payload builder in the seam.
            var p = TestCommandUiAction.BuildDescribePayload("OTHER", false, null);
            Assert.Equal("0", Value(p, "count"));
            Assert.Equal(4, p.Count);
        }

        [Fact]
        public void SceneTokens_CoverEveryDispatchScene()
        {
            Assert.Equal("FLIGHT", TestCommandUiAction.SceneToken(TestCommandScene.Flight));
            Assert.Equal("SPACECENTER",
                TestCommandUiAction.SceneToken(TestCommandScene.SpaceCenter));
            Assert.Equal("TRACKSTATION",
                TestCommandUiAction.SceneToken(TestCommandScene.TrackingStation));
            Assert.Equal("EDITOR", TestCommandUiAction.SceneToken(TestCommandScene.Editor));
            Assert.Equal("MAINMENU", TestCommandUiAction.SceneToken(TestCommandScene.MainMenu));
            Assert.Equal("LOADING", TestCommandUiAction.SceneToken(TestCommandScene.Loading));
            Assert.Equal("OTHER", TestCommandUiAction.SceneToken(TestCommandScene.Other));
        }

        // ----- reason-token hygiene -----

        [Fact]
        public void EveryReasonTokenIsDistinct()
        {
            // A shared token would make two different faults indistinguishable in a spec's
            // `expect`, which is the whole reason each one is named.
            string[] tokens =
            {
                TestCommandUiAction.OpArgMissingReason,
                TestCommandUiAction.OpArgInvalidReason,
                TestCommandUiAction.WindowArgMissingReason,
                TestCommandUiAction.WindowUnknownReason,
                TestCommandUiAction.WindowNotInSceneReason,
                TestCommandUiAction.TabArgMissingReason,
                TestCommandUiAction.TabUnknownReason,
                TestCommandUiAction.WindowHasNoTabsReason,
                TestCommandUiAction.ModeArgMissingReason,
                TestCommandUiAction.ModeArgInvalidReason,
                TestCommandUiAction.RectArgMissingReason,
                TestCommandUiAction.RectArgInvalidReason,
                TestCommandUiAction.HostUnavailableReason,
                TestCommandUiAction.ComplexityRefusedRecordingReason,
                TestCommandUiAction.ComplexityNotAppliedReason,
                TestCommandUiAction.WindowNotToggledReason,
                TestCommandUiAction.WindowSelfClosedReason,
                TestCommandUiAction.NotSettledReason,
                TestCommandUiAction.WindowHostHiddenReason,
                TestCommandUiAction.TabNotAppliedReason,
                TestCommandUiAction.RectNotAppliedReason,
                TestCommandUiAction.ThrewReason,
            };
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
            foreach (string token in tokens)
            {
                Assert.False(string.IsNullOrWhiteSpace(token));
                // No whitespace or '=': the response writer would percent-encode either and
                // a spec's substring match on the reason would then miss.
                Assert.DoesNotContain(" ", token);
                Assert.DoesNotContain("=", token);
            }
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.First(kv => kv.Key == key).Value;

        private static string[] TabsOf(string window)
        {
            TestCommandUiAction.TryResolveWindow(window, out UiWindowSpec spec, out _);
            return spec.Tabs ?? new string[0];
        }

        /// <summary>Scoped OS-culture swap: the invariance cells above must prove the
        /// production site is invariant, not that the host happens to be.</summary>
        private sealed class CultureSwap : System.IDisposable
        {
            private readonly CultureInfo previous;

            internal CultureSwap(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    CultureInfo.GetCultureInfo(name);
            }

            public void Dispose()
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
