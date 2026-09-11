using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the six GUI-census ops added on top of the original
    /// <c>UiAction</c> vocabulary: <c>pointer</c>, <c>find</c>, <c>expand</c>,
    /// <c>target</c>, <c>picker</c> and <c>dialog</c>, plus <c>op=rect</c>'s clamp to each
    /// window's own minimum.
    ///
    /// <para>The op registration itself (token round-trip, enum stability,
    /// <c>OpNeedsWindow</c>, <c>OpIsTwoPhase</c>) is pinned by
    /// <c>TestCommandUiActionTests</c>, which owns the whole-vocabulary cells; this file
    /// covers the per-op parses, geometry and payloads.</para>
    /// </summary>
    public class TestCommandUiCensusOpsTests
    {
        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.First(kv => kv.Key == key).Value;

        // ================================================================ op=rect clamp

        [Fact]
        public void RectClamp_RaisesBothAxesToTheWindowsOwnFloor()
        {
            // The defect this exists for, with the census's own numbers: GUI-1 commanded
            // w=1280 (the instance width) on a window whose MinWindowWidth is 1410, and
            // nothing clamped it - ParsekUI.HandleResizeDrag is the program's only clamp and
            // it runs during a DRAG. The picture was a squeezed layout no player can
            // produce.
            var commanded = new UiActionRect { X = 0f, Y = 8f, W = 1280f, H = 700f };
            UiActionRect applied = TestCommandUiAction.ClampRectToMinimums(
                commanded, 1410f, 500f, out bool clamped);
            Assert.True(clamped);
            Assert.Equal(1410f, applied.W);
            // The height was already above its floor, so it is untouched: the clamp is
            // per-axis, not all-or-nothing.
            Assert.Equal(700f, applied.H);
            Assert.Equal(0f, applied.X);
            Assert.Equal(8f, applied.Y);
        }

        [Fact]
        public void RectClamp_LeavesAGenerousRectAlone_AndReportsNoClamp()
        {
            var commanded = new UiActionRect { X = 10f, Y = 20f, W = 1600f, H = 900f };
            UiActionRect applied = TestCommandUiAction.ClampRectToMinimums(
                commanded, 1410f, 500f, out bool clamped);
            Assert.False(clamped);
            Assert.Equal(1600f, applied.W);
            Assert.Equal(900f, applied.H);
        }

        [Fact]
        public void RectClamp_IsInertForAWindowWithNoMinimum()
        {
            // main / settings / gloops have no resize handle, so there is no drag floor to
            // reproduce and a zero minimum must not be read as "clamp to zero".
            var commanded = new UiActionRect { X = 8f, Y = 8f, W = 250f, H = 700f };
            UiActionRect applied = TestCommandUiAction.ClampRectToMinimums(
                commanded, 0f, 0f, out bool clamped);
            Assert.False(clamped);
            Assert.Equal(250f, applied.W);
            Assert.Equal(700f, applied.H);
        }

        [Fact]
        public void RectClamp_OnlyRaises_NeverLowers()
        {
            // The MIRROR DIRECTION, checked rather than assumed: a window commanded SMALLER
            // than its floor comes back at the floor, and one commanded LARGER is never
            // pulled down to it.
            UiActionRect up = TestCommandUiAction.ClampRectToMinimums(
                new UiActionRect { W = 100f, H = 50f }, 420f, 160f, out bool clampedUp);
            Assert.True(clampedUp);
            Assert.Equal(420f, up.W);
            Assert.Equal(160f, up.H);

            UiActionRect down = TestCommandUiAction.ClampRectToMinimums(
                new UiActionRect { W = 900f, H = 900f }, 420f, 160f, out bool clampedDown);
            Assert.False(clampedDown);
            Assert.Equal(900f, down.W);
            Assert.Equal(900f, down.H);
        }

        [Fact]
        public void RectPayload_CarriesTheClampAndTheFloorThatCausedIt()
        {
            var p = TestCommandUiAction.BuildRectPayload(
                "logistics", new UiActionRect { X = 0f, Y = 8f, W = 1410f, H = 700f },
                clamped: true, minW: 1410f, minH: 500f);
            Assert.Equal("0,8,1410,700", Value(p, "rect"));
            Assert.Equal("true", Value(p, "clamped"));
            Assert.Equal("1410", Value(p, "minW"));
            Assert.Equal("500", Value(p, "minH"));
        }

        [Theory]
        [InlineData(0f, 0f, "-")]
        [InlineData(1410f, 500f, "1410,500")]
        [InlineData(320f, 600f, "320,600")]
        public void MinSizeFormatsAsADashOnlyWhenThereIsNoMinimum(
            float minW, float minH, string expected)
        {
            Assert.Equal(expected, TestCommandUiAction.FormatMinSize(minW, minH));
        }

        [Fact]
        public void SettleChecksHostShowUi_IsExactlyOpenAndRect()
        {
            // The asymmetry is the point. open / rect read back a FIELD, so a frame that
            // never reached the window compares the written value with itself. The five
            // later two-phase ops read a captured tree, the OS cursor, or a collection -
            // none of which a hidden host can fake - and applying the gate to them would
            // refuse a `pointer park` issued deliberately with the surface hidden.
            Assert.True(TestCommandUiAction.SettleChecksHostShowUi(UiActionOp.Open));
            Assert.True(TestCommandUiAction.SettleChecksHostShowUi(UiActionOp.Rect));
            foreach (UiActionOp op in new[] { UiActionOp.Pointer, UiActionOp.Find,
                                              UiActionOp.Expand, UiActionOp.Target,
                                              UiActionOp.Picker, UiActionOp.Dialog })
            {
                Assert.False(TestCommandUiAction.SettleChecksHostShowUi(op));
            }
        }

        // ================================================================ op=pointer

        [Fact]
        public void Pointer_TakesEitherACoordinatePairOrPark_NeverNeither()
        {
            Assert.False(TestCommandUiPointer.TryParseRequest(
                null, null, null, out _, out string reason));
            Assert.Equal(TestCommandUiPointer.ArgMissingReason, reason);
        }

        [Fact]
        public void Pointer_RefusesAHalfCoordinate()
        {
            // A lone x is a missing arg, not a half-move: moving the cursor to a y the
            // caller did not name would silently change what every later capture shows.
            Assert.False(TestCommandUiPointer.TryParseRequest(
                "100", null, null, out _, out string reason));
            Assert.Equal(TestCommandUiPointer.ArgMissingReason, reason);
        }

        [Fact]
        public void Pointer_RefusesParkAndCoordinatesTogether()
        {
            Assert.False(TestCommandUiPointer.TryParseRequest(
                "100", "200", "true", out _, out string reason));
            Assert.Equal(TestCommandUiPointer.ArgConflictReason, reason);
        }

        [Fact]
        public void Pointer_ParkGoesToTheClientCorner()
        {
            Assert.True(TestCommandUiPointer.TryParseRequest(
                null, null, "true", out UiPointerRequest req, out _));
            Assert.True(req.Park);
            Assert.Equal(TestCommandUiPointer.ParkX, req.X);
            Assert.Equal(TestCommandUiPointer.ParkY, req.Y);
        }

        [Fact]
        public void Pointer_ParkFalseIsAnOrdinaryMove_SoAGeneratedSpecCanAlwaysCarryTheKey()
        {
            Assert.True(TestCommandUiPointer.TryParseRequest(
                "40", "60", "false", out UiPointerRequest req, out _));
            Assert.False(req.Park);
            Assert.Equal(40f, req.X);
            Assert.Equal(60f, req.Y);
        }

        [Theory]
        [InlineData("True")]     // case-sensitive, like every other token in the grammar
        [InlineData("1")]
        [InlineData("yes")]
        public void Pointer_ParkIsCaseSensitiveAndClosed(string raw)
        {
            Assert.False(TestCommandUiPointer.TryParseRequest(
                null, null, raw, out _, out string reason));
            Assert.Equal(TestCommandUiPointer.ArgInvalidReason, reason);
        }

        [Theory]
        [InlineData("abc", "10")]
        [InlineData("10", "abc")]
        [InlineData("1e9", "10")]
        [InlineData("NaN", "10")]
        public void Pointer_CoordinatesMustBeFiniteInvariantFloatsInRange(string x, string y)
        {
            Assert.False(TestCommandUiPointer.TryParseRequest(
                x, y, null, out _, out string reason));
            Assert.Equal(TestCommandUiPointer.ArgInvalidReason, reason);
        }

        [Fact]
        public void Pointer_CoordinateParseIsCultureInvariant()
        {
            // A comma decimal is NOT a valid coordinate even on a ro-RO host: the seam
            // parses with InvariantCulture, so the spec grammar is dot-decimal everywhere.
            Assert.True(TestCommandUiPointer.TryParseRequest(
                "640.5", "360.5", null, out UiPointerRequest ok, out _));
            Assert.Equal(640.5f, ok.X);
            Assert.False(TestCommandUiPointer.TryParseRequest(
                "640,5", "360", null, out _, out _));
        }

        [Theory]
        [InlineData(0f, 0f, true)]
        [InlineData(1279f, 719f, true)]
        [InlineData(1280f, 360f, false)]   // half-open: x == width is the next column
        [InlineData(640f, 720f, false)]
        [InlineData(-1f, 360f, false)]
        public void Pointer_ClientBoxIsHalfOpenOnTheFarEdges(float x, float y, bool inside)
        {
            Assert.Equal(inside, TestCommandUiPointer.IsInsideClient(x, y, 1280, 720));
        }

        [Fact]
        public void Pointer_ClientBoxIsEmptyWhenTheScreenIsUnreadable()
        {
            Assert.False(TestCommandUiPointer.IsInsideClient(0f, 0f, 0, 0));
        }

        [Fact]
        public void Pointer_YFlipIsTheSameExpressionUnityUsesForTheImguiPass()
        {
            // Event.current.mousePosition.y == Screen.height - Input.mousePosition.y. The
            // wire is top-down because that is the frame the GUI-tree dump reports, so an
            // op=find answer chains straight into op=pointer.
            Assert.Equal(720f, TestCommandUiPointer.GuiYFromUnityMouseY(720, 0f));
            Assert.Equal(0f, TestCommandUiPointer.GuiYFromUnityMouseY(720, 720f));
            Assert.Equal(360f, TestCommandUiPointer.GuiYFromUnityMouseY(720, 360f));
        }

        [Fact]
        public void Pointer_ReadBackToleranceAbsorbsTheYFlipsOffByOne_AndNothingMore()
        {
            Assert.True(TestCommandUiPointer.LandedWithinTolerance(100f, 200f, 100f, 200f));
            Assert.True(TestCommandUiPointer.LandedWithinTolerance(100f, 200f, 102f, 198f));
            Assert.False(TestCommandUiPointer.LandedWithinTolerance(100f, 200f, 103f, 200f));
            Assert.False(TestCommandUiPointer.LandedWithinTolerance(100f, 200f, 100f, 205f));
        }

        [Fact]
        public void PointerPayload_ReportsTheReadBackAndHowTheWindowWasFound()
        {
            var p = TestCommandUiPointer.BuildPayload(
                observedX: 640f, observedGuiY: 360f, park: false,
                screenX: 648, screenY: 391, via: "active");
            Assert.Equal("pointer", Value(p, "op"));
            Assert.Equal("640", Value(p, "x"));
            Assert.Equal("360", Value(p, "y"));
            Assert.Equal("false", Value(p, "park"));
            Assert.Equal("648", Value(p, "sx"));
            Assert.Equal("391", Value(p, "sy"));
            Assert.Equal("active", Value(p, "via"));
        }

        [Fact]
        public void PointerPayload_KeysAreAllCapturable()
        {
            // hlib.HANDLE_REF_RE captures ${step.<field>} only for [A-Za-z0-9_]+, so a key
            // with a dash or a dot would be silently unreachable from a spec - which is the
            // whole point of these keys.
            var p = TestCommandUiPointer.BuildPayload(0f, 0f, true, 0, 0, "active");
            foreach (KeyValuePair<string, string> kv in p)
                Assert.Matches("^[A-Za-z0-9_]+$", kv.Key);
        }

        // ================================================================ op=find

        private static UiFindCandidate Candidate(string text, string kind,
                                                 float x = 0f, float y = 0f,
                                                 float w = 10f, float h = 10f)
            => new UiFindCandidate { Text = text, Kind = kind, X = x, Y = y, W = w, H = h };

        [Fact]
        public void Find_RequiresText_AndRefusesTheEmptyString()
        {
            Assert.False(TestCommandUiFind.TryParseText(null, out _, out string r1));
            Assert.Equal(TestCommandUiFind.TextArgMissingReason, r1);
            // Empty is refused with the SAME reason on purpose: an empty query
            // prefix-matches every node, so it is not a weaker request but a meaningless
            // one.
            Assert.False(TestCommandUiFind.TryParseText("", out _, out string r2));
            Assert.Equal(TestCommandUiFind.TextArgMissingReason, r2);
        }

        [Fact]
        public void Find_CtrlIsOptionalAndClosed()
        {
            Assert.True(TestCommandUiFind.TryParseCtrl(null, out string none, out _));
            Assert.Null(none);
            Assert.True(TestCommandUiFind.TryParseCtrl("button", out string b, out _));
            Assert.Equal("button", b);
            Assert.False(TestCommandUiFind.TryParseCtrl("Button", out _, out string reason));
            Assert.Equal(TestCommandUiFind.CtrlArgInvalidReason, reason);
        }

        [Fact]
        public void Find_CtrlVocabularyMirrorsTheDumpsOwnKindNames()
        {
            // The filter must speak the same words the .gui.json a reviewer is reading
            // does, or a spec author reads one vocabulary and writes another.
            foreach (string ctrl in TestCommandUiFind.CtrlValues)
                Assert.Contains(ctrl, TestCommandUiFind.ValidCtrlNames.Split(','));
            Assert.Equal(13, TestCommandUiFind.CtrlValues.Length);
        }

        [Fact]
        public void Find_IndexDefaultsToTheFirstMatch()
        {
            Assert.True(TestCommandUiFind.TryParseIndex(null, out int i, out _));
            Assert.Equal(0, i);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("two")]
        [InlineData("1.5")]
        public void Find_IndexMustBeANonNegativeInteger(string raw)
        {
            Assert.False(TestCommandUiFind.TryParseIndex(raw, out _, out string reason));
            Assert.Equal(TestCommandUiFind.IndexArgInvalidReason, reason);
        }

        [Fact]
        public void Find_TheLadderTakesTheFirstRungWithAnyHit()
        {
            // THE property. A window with both "Close" and "Close all" must answer the
            // exact one for text=Close; pooling the rungs would make the answer depend on
            // draw order, and a spec pinned against it would silently re-aim the moment a
            // row was added.
            var candidates = new List<UiFindCandidate>
            {
                Candidate("Close all", "button", 10f, 10f),
                Candidate("Close", "button", 50f, 10f),
            };
            Assert.True(TestCommandUiFind.TryPick(
                candidates, "Close", null, 0, out UiFindCandidate match,
                out UiFindMatchMode mode, out int rung, out int searched, out _));
            Assert.Equal(UiFindMatchMode.Exact, mode);
            Assert.Equal("Close", match.Text);
            Assert.Equal(1, rung);
            Assert.Equal(2, searched);
        }

        [Fact]
        public void Find_FallsToPrefixThenContains()
        {
            var candidates = new List<UiFindCandidate>
            {
                Candidate("Wipe Recordings...", "button"),
                Candidate("Confirm: Wipe", "label"),
            };
            Assert.True(TestCommandUiFind.TryPick(
                candidates, "Wipe", null, 0, out UiFindCandidate prefixHit,
                out UiFindMatchMode prefixMode, out _, out _, out _));
            Assert.Equal(UiFindMatchMode.Prefix, prefixMode);
            Assert.Equal("Wipe Recordings...", prefixHit.Text);

            Assert.True(TestCommandUiFind.TryPick(
                candidates, "Confirm", null, 0, out UiFindCandidate containsHit,
                out UiFindMatchMode containsMode, out _, out _, out _));
            Assert.Equal(UiFindMatchMode.Prefix, containsMode);
            Assert.Equal("Confirm: Wipe", containsHit.Text);

            Assert.True(TestCommandUiFind.TryPick(
                candidates, "Record", null, 0, out UiFindCandidate inner,
                out UiFindMatchMode innerMode, out _, out _, out _));
            Assert.Equal(UiFindMatchMode.Contains, innerMode);
            Assert.Equal("Wipe Recordings...", inner.Text);
        }

        [Fact]
        public void Find_IsCaseSensitive()
        {
            var candidates = new List<UiFindCandidate> { Candidate("Close", "button") };
            Assert.False(TestCommandUiFind.TryPick(
                candidates, "close", null, 0, out _, out _, out _, out _,
                out string reason));
            Assert.Equal(TestCommandUiFind.ControlNotFoundReason, reason);
        }

        [Fact]
        public void Find_CtrlFilterNarrowsTheSearchedSet()
        {
            var candidates = new List<UiFindCandidate>
            {
                Candidate("Log", "label", 0f, 0f),
                Candidate("Log", "button", 80f, 0f),
            };
            Assert.True(TestCommandUiFind.TryPick(
                candidates, "Log", "button", 0, out UiFindCandidate match, out _,
                out int rung, out int searched, out _));
            Assert.Equal(80f, match.X);
            Assert.Equal(1, rung);
            Assert.Equal(1, searched);
        }

        [Fact]
        public void Find_IndexSelectsWithinTheWinningRungInDrawOrder()
        {
            var candidates = new List<UiFindCandidate>
            {
                Candidate("G", "button", 10f, 30f),
                Candidate("G", "button", 10f, 60f),
                Candidate("G", "button", 10f, 90f),
            };
            Assert.True(TestCommandUiFind.TryPick(
                candidates, "G", "button", 2, out UiFindCandidate third, out _,
                out int rung, out _, out _));
            Assert.Equal(90f, third.Y);
            Assert.Equal(3, rung);
        }

        [Fact]
        public void Find_IndexPastTheEndIsItsOwnError_NotControlNotFound()
        {
            // "There are 2 of these, you asked for the 5th" and "no such control" send an
            // author to different fixes.
            var candidates = new List<UiFindCandidate>
            {
                Candidate("G", "button"), Candidate("G", "button"),
            };
            Assert.False(TestCommandUiFind.TryPick(
                candidates, "G", null, 5, out _, out UiFindMatchMode mode, out int rung,
                out _, out string reason));
            Assert.Equal(TestCommandUiFind.IndexOutOfRangeReason, reason);
            Assert.Equal(UiFindMatchMode.Exact, mode);
            Assert.Equal(2, rung);
        }

        [Fact]
        public void Find_AnEmptyWindowAnswersControlNotFoundWithAZeroSearchedCount()
        {
            Assert.False(TestCommandUiFind.TryPick(
                new List<UiFindCandidate>(), "Close", null, 0, out _, out _, out _,
                out int searched, out string reason));
            Assert.Equal(TestCommandUiFind.ControlNotFoundReason, reason);
            Assert.Equal(0, searched);
        }

        [Fact]
        public void Find_CentreIsTheRectsMiddle()
        {
            TestCommandUiFind.CentreOf(
                Candidate("x", "button", 100f, 200f, 60f, 20f), out float cx, out float cy);
            Assert.Equal(130f, cx);
            Assert.Equal(210f, cy);
        }

        [Fact]
        public void FindPayload_CarriesTheNodesOwnTextAndACapturableCentre()
        {
            var p = TestCommandUiFind.BuildPayload(
                "settings", Candidate("Wipe Recordings...", "button", 300f, 420f, 140f, 24f),
                UiFindMatchMode.Prefix, 1);
            Assert.Equal("find", Value(p, "op"));
            Assert.Equal("settings", Value(p, "window"));
            // The node's OWN text, not the query: a prefix hit must tell the reader what it
            // actually landed on.
            Assert.Equal("Wipe Recordings...", Value(p, "text"));
            Assert.Equal("button", Value(p, "ctrl"));
            Assert.Equal("prefix", Value(p, "match"));
            Assert.Equal("1", Value(p, "matches"));
            Assert.Equal("300", Value(p, "x"));
            Assert.Equal("420", Value(p, "y"));
            Assert.Equal("140", Value(p, "w"));
            Assert.Equal("24", Value(p, "h"));
            Assert.Equal("370", Value(p, "cx"));
            Assert.Equal("432", Value(p, "cy"));
            foreach (KeyValuePair<string, string> kv in p)
                Assert.Matches("^[A-Za-z0-9_]+$", kv.Key);
        }

        [Fact]
        public void FindMatchTokensArePinned()
        {
            Assert.Equal("exact", TestCommandUiFind.MatchToken(UiFindMatchMode.Exact));
            Assert.Equal("prefix", TestCommandUiFind.MatchToken(UiFindMatchMode.Prefix));
            Assert.Equal("contains", TestCommandUiFind.MatchToken(UiFindMatchMode.Contains));
            Assert.Equal("none", TestCommandUiFind.MatchToken(UiFindMatchMode.None));
        }

        // ================================================================ op=expand

        [Fact]
        public void Expand_OnlyTwoWindowsKeepDriveableExpansionState()
        {
            Assert.NotNull(TestCommandUiState.ExpandPrefixesFor(
                TestCommandUiAction.MissionsWindow));
            Assert.NotNull(TestCommandUiState.ExpandPrefixesFor(
                TestCommandUiAction.LogisticsWindow));
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
            {
                if (spec.Name == TestCommandUiAction.MissionsWindow
                    || spec.Name == TestCommandUiAction.LogisticsWindow)
                {
                    continue;
                }
                Assert.Null(TestCommandUiState.ExpandPrefixesFor(spec.Name));
            }
        }

        [Fact]
        public void Expand_AnUnsupportedWindowIsNamedRatherThanSilentlyInert()
        {
            Assert.False(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.TimelineWindow, "group:Apollo", stateGiven: false,
                scope: out _, prefix: out _, value: out _, rejectReason: out string reason));
            Assert.Equal(TestCommandUiState.ExpandUnsupportedWindowReason, reason);
            Assert.Contains(TestCommandUiAction.MissionsWindow,
                TestCommandUiState.ExpandableWindowNames.Split(','));
        }

        [Fact]
        public void Expand_KeyIsRequired()
        {
            Assert.False(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, null, stateGiven: false,
                scope: out _, prefix: out _, value: out _,
                rejectReason: out string reason));
            Assert.Equal(TestCommandUiState.ExpandKeyArgMissingReason, reason);
        }

        [Fact]
        public void Expand_BulkTokensAreScopesRatherThanKeys()
        {
            Assert.True(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, TestCommandUiState.ExpandAllToken,
                stateGiven: false, scope: out UiExpandScope all, prefix: out string allPrefix,
                value: out string allValue, rejectReason: out _));
            Assert.Equal(UiExpandScope.All, all);
            Assert.Null(allPrefix);
            Assert.Null(allValue);

            Assert.True(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, TestCommandUiState.ExpandNoneToken,
                stateGiven: false, scope: out UiExpandScope none,
                prefix: out string nonePrefix, value: out string noneValue,
                rejectReason: out _));
            Assert.Equal(UiExpandScope.None, none);
            Assert.Null(nonePrefix);
            Assert.Null(noneValue);
        }

        [Fact]
        public void Expand_ABulkKeyWithAnExplicitStateIsRefused_NotSilentlyOverridden()
        {
            // The bulk tokens CARRY their direction, so a `state=` beside one either agrees
            // redundantly or contradicts it - and letting the token win silently would make
            // a step that reads "collapse everything" expand everything. Refused, the
            // op=pointer park-versus-coordinates rule.
            foreach (string bulk in new[] { TestCommandUiState.ExpandAllToken,
                                            TestCommandUiState.ExpandNoneToken })
            {
                Assert.False(TestCommandUiState.TryParseExpandKey(
                    TestCommandUiAction.MissionsWindow, bulk, stateGiven: true,
                    scope: out _, prefix: out _, value: out _,
                    rejectReason: out string reason));
                Assert.Equal(TestCommandUiState.ExpandStateWithBulkKeyReason, reason);
            }
            // A SINGLE key with a state is the ordinary case and stays legal.
            Assert.True(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, "group:Apollo", stateGiven: true,
                scope: out UiExpandScope scope, prefix: out string prefix,
                value: out string value, rejectReason: out _));
            Assert.Equal(UiExpandScope.Single, scope);
            Assert.Equal("group", prefix);
            Assert.Equal("Apollo", value);
        }

        [Fact]
        public void Expand_KeySplitsAtTheFirstColonSoTheValueMayCarryMore()
        {
            // A vessel key IS "missionId:headId", so the value half has its own colon. A
            // split on the LAST colon (or on every colon) would address the wrong row.
            Assert.True(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, "vessel:m17:head4", stateGiven: false,
                scope: out UiExpandScope scope, prefix: out string prefix,
                value: out string value, rejectReason: out _));
            Assert.Equal(UiExpandScope.Single, scope);
            Assert.Equal("vessel", prefix);
            Assert.Equal("m17:head4", value);
        }

        [Theory]
        [InlineData("group")]         // no colon at all
        [InlineData(":Apollo")]       // empty prefix
        [InlineData("group:")]        // empty value
        [InlineData("Group:Apollo")]  // case-sensitive
        [InlineData("row:x")]         // a logistics prefix on the missions window
        public void Expand_AMalformedOrForeignKeyIsRejectedWithTheWindowsOwnPrefixList(
            string raw)
        {
            Assert.False(TestCommandUiState.TryParseExpandKey(
                TestCommandUiAction.MissionsWindow, raw, stateGiven: false,
                scope: out _, prefix: out _, value: out _,
                rejectReason: out string reason));
            Assert.Equal(TestCommandUiState.ExpandKeyInvalidReason, reason);
            string prefixes = TestCommandUiState.ExpandPrefixNamesFor(
                TestCommandUiAction.MissionsWindow);
            Assert.Equal(5, prefixes.Split(',').Length);
        }

        [Fact]
        public void Expand_TheMissionsWindowCoversBothOfItsTabs()
        {
            // ONE window token, five collections: the Recordings tab's group folders and
            // chain blocks, and the Missions tab's vessel / leg / digest rows.
            string[] prefixes = TestCommandUiState.ExpandPrefixesFor(
                TestCommandUiAction.MissionsWindow);
            Assert.Equal(
                new[] { "group", "chain", "vessel", "leg", "digest" }, prefixes);
        }

        [Fact]
        public void Expand_StateDefaultsToExpanding()
        {
            Assert.True(TestCommandUiState.TryParseState(null, out bool expanded, out _));
            Assert.True(expanded);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void Expand_StateIsAClosedSet(string raw, bool want)
        {
            Assert.True(TestCommandUiState.TryParseState(raw, out bool got, out _));
            Assert.Equal(want, got);
        }

        [Fact]
        public void Expand_StateIsCaseSensitive()
        {
            Assert.False(TestCommandUiState.TryParseState("True", out _, out string reason));
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, reason);
        }

        [Fact]
        public void ExpandPayload_ReportsBothItsOwnEffectAndTheStandingState()
        {
            // changed=0 on a key=all step means the window was already in that state, which
            // reads differently from changed=16 in a capture that came out looking the same.
            var p = TestCommandUiState.BuildExpandPayload(
                "missions", "all", expanded: true, changed: 16, expandedCount: 16,
                total: 21);
            Assert.Equal("expand", Value(p, "op"));
            Assert.Equal("missions", Value(p, "window"));
            Assert.Equal("all", Value(p, "key"));
            Assert.Equal("true", Value(p, "state"));
            Assert.Equal("16", Value(p, "changed"));
            Assert.Equal("16", Value(p, "expanded"));
            Assert.Equal("21", Value(p, "total"));
        }

        [Fact]
        public void CandidateListsAreCappedWithACountedTail()
        {
            // The list is unbounded in the save (a dense career carries hundreds of group
            // names) and the response is ONE line, so a reject lists a few and counts the
            // rest rather than being truncated by the reader.
            var many = new List<string>();
            for (int i = 0; i < 20; i++)
                many.Add("g" + i.ToString(CultureInfo.InvariantCulture));
            string formatted = TestCommandUiState.FormatCandidates(
                many, TestCommandUiState.CandidateListCap);
            Assert.StartsWith("g0,g1", formatted);
            Assert.EndsWith("(+12 more)", formatted);
            Assert.Equal("-", TestCommandUiState.FormatCandidates(
                new List<string>(), TestCommandUiState.CandidateListCap));
        }

        // ================================================================ op=target

        [Fact]
        public void Target_OnlyTheStructureWindowHasATargetedOpener()
        {
            Assert.False(TestCommandUiState.TryParseTarget(
                TestCommandUiAction.MissionsWindow, "Apollo", null, out _, out _,
                out string reason));
            Assert.Equal(TestCommandUiState.TargetUnsupportedWindowReason, reason);
        }

        [Fact]
        public void Target_NeedsExactlyOneSelector()
        {
            Assert.False(TestCommandUiState.TryParseTarget(
                TestCommandUiAction.StructureWindow, null, null, out _, out _,
                out string missing));
            Assert.Equal(TestCommandUiState.TargetArgMissingReason, missing);

            // BOTH is refused rather than resolved by precedence: the two open different
            // lists, and guessing would photograph the wrong one under the caller's label.
            Assert.False(TestCommandUiState.TryParseTarget(
                TestCommandUiAction.StructureWindow, "Apollo", "R-1", out _, out _,
                out string conflict));
            Assert.Equal(TestCommandUiState.TargetArgConflictReason, conflict);
        }

        [Fact]
        public void Target_ResolvesEachSelectorToItsOwnKind()
        {
            Assert.True(TestCommandUiState.TryParseTarget(
                TestCommandUiAction.StructureWindow, "Apollo", null,
                out UiTargetKind mission, out string missionValue, out _));
            Assert.Equal(UiTargetKind.Mission, mission);
            Assert.Equal("Apollo", missionValue);
            Assert.Equal("mission", TestCommandUiState.TargetKindToken(mission));

            Assert.True(TestCommandUiState.TryParseTarget(
                TestCommandUiAction.StructureWindow, null, "Mun Depot",
                out UiTargetKind route, out string routeValue, out _));
            Assert.Equal(UiTargetKind.Route, route);
            Assert.Equal("Mun Depot", routeValue);
            Assert.Equal("route", TestCommandUiState.TargetKindToken(route));
        }

        [Fact]
        public void TargetPayload_CarriesTheRowCountTheOpenerProduced()
        {
            // The difference between the empty chrome the first census photographed and a
            // populated Log, without reading the PNG.
            var p = TestCommandUiState.BuildTargetPayload(
                "structure", UiTargetKind.Mission, "T-3", "Apollo 11", 14, true);
            Assert.Equal("target", Value(p, "op"));
            Assert.Equal("mission", Value(p, "target"));
            Assert.Equal("T-3", Value(p, "id"));
            Assert.Equal("Apollo 11", Value(p, "title"));
            Assert.Equal("14", Value(p, "steps"));
            Assert.Equal("true", Value(p, "open"));
        }

        // ================================================================ op=picker

        [Fact]
        public void Picker_MissionsTakesAGroupOrARecording()
        {
            Assert.True(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.MissionsWindow, "Apollo", null, null,
                out UiPickerMode setParent, out string group, out _));
            Assert.Equal(UiPickerMode.SetParent, setParent);
            Assert.Equal("Apollo", group);
            Assert.Equal("setparent", TestCommandUiState.PickerModeToken(setParent));

            Assert.True(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.MissionsWindow, null, "first", null,
                out UiPickerMode manage, out string recording, out _));
            Assert.Equal(UiPickerMode.Manage, manage);
            Assert.Equal("first", recording);
            Assert.Equal("manage", TestCommandUiState.PickerModeToken(manage));
        }

        [Fact]
        public void Picker_LogisticsTakesARoute()
        {
            Assert.True(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.LogisticsWindow, null, null, "Mun Depot",
                out UiPickerMode link, out string route, out _));
            Assert.Equal(UiPickerMode.Link, link);
            Assert.Equal("Mun Depot", route);
            Assert.Equal("link", TestCommandUiState.PickerModeToken(link));
        }

        [Fact]
        public void Picker_RefusesASelectorTheNamedWindowDoesNotOwn()
        {
            // route= on the missions window and group= on the logistics window are
            // conflicts rather than "missing": the caller named a selector, just not one
            // that window's popup takes.
            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.MissionsWindow, null, null, "Mun Depot", out _, out _,
                out string r1));
            Assert.Equal(TestCommandUiState.PickerArgConflictReason, r1);

            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.LogisticsWindow, "Apollo", null, null, out _, out _,
                out string r2));
            Assert.Equal(TestCommandUiState.PickerArgConflictReason, r2);
        }

        [Fact]
        public void Picker_RefusesTwoMissionsSelectorsAtOnce()
        {
            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.MissionsWindow, "Apollo", "rec-1", null, out _, out _,
                out string reason));
            Assert.Equal(TestCommandUiState.PickerArgConflictReason, reason);
        }

        [Fact]
        public void Picker_NeedsASelector()
        {
            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.MissionsWindow, null, null, null, out _, out _,
                out string r1));
            Assert.Equal(TestCommandUiState.PickerArgMissingReason, r1);
            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.LogisticsWindow, null, null, null, out _, out _,
                out string r2));
            Assert.Equal(TestCommandUiState.PickerArgMissingReason, r2);
        }

        [Fact]
        public void Picker_AnUnsupportedWindowIsNamed()
        {
            Assert.False(TestCommandUiState.TryParsePicker(
                TestCommandUiAction.SettingsWindow, "Apollo", null, null, out _, out _,
                out string reason));
            Assert.Equal(TestCommandUiState.PickerUnsupportedWindowReason, reason);
            Assert.Equal(2, TestCommandUiState.PickerWindowNames.Split(',').Length);
        }

        [Fact]
        public void PickerPayload_NamesWhichPopupOpened()
        {
            var p = TestCommandUiState.BuildPickerPayload(
                "missions", UiPickerMode.SetParent, "Apollo", true);
            Assert.Equal("picker", Value(p, "op"));
            Assert.Equal("missions", Value(p, "window"));
            Assert.Equal("setparent", Value(p, "picker"));
            Assert.Equal("Apollo", Value(p, "target"));
            Assert.Equal("true", Value(p, "open"));
        }

        // ================================================================ op=dialog

        [Fact]
        public void Dialog_TheAnswerArgDefaultsToTheTreeMergeDialog()
        {
            Assert.True(TestCommandUiDialog.TryParseAnswerDialog(
                null, out string token, out _));
            Assert.Equal(TestCommandUiDialog.MergeDialogToken, token);
        }

        [Fact]
        public void Dialog_TheAnswerArgIsAClosedSet_SoAWrongValueCannotFallBack()
        {
            // The pre-switch dialog is deliberately NOT answerable: AnswerMergeDialog
            // completes on the post-answer scene settling out of FLIGHT, and that dialog's
            // buttons end in SetActiveVessel, which for a loaded target changes no scene.
            // A silent fallback to the merge dialog is exactly the confusion the separate
            // dialog name removed.
            Assert.False(TestCommandUiDialog.TryParseAnswerDialog(
                "preswitch", out _, out string reason));
            Assert.Equal(TestCommandUiDialog.DialogArgInvalidReason, reason);
            Assert.False(TestCommandUiDialog.TryParseAnswerDialog("Merge", out _, out _));
        }

        [Fact]
        public void Dialog_TheTwoMergeDialogNamesAreDistinct()
        {
            // THE FIX. Three spawn sites shared one name, and AnswerMergeDialog selects its
            // button BY ORDER - so a live pre-switch popup would have had its merge action
            // invoked by a step concluding a re-fly.
            Assert.NotEqual(MergeDialog.DialogName, MergeDialog.PreSwitchDialogName);
            // Both still scan as Parsek's, so op=dialog reports either one.
            Assert.True(TestCommandUiDialog.IsParsekDialogName(MergeDialog.DialogName));
            Assert.True(TestCommandUiDialog.IsParsekDialogName(
                MergeDialog.PreSwitchDialogName));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("SomeStockDialog", false)]
        [InlineData("parsekMerge", false)]   // case-sensitive
        [InlineData("ParsekUFSealDialog", true)]
        public void Dialog_ParsekOwnershipIsAPrefixScan(string name, bool owned)
        {
            // Scanned by prefix rather than against a list of the 21 spawn sites: a list
            // would go stale the first time a dialog was added, and its failure mode is
            // "no dialog open" reported over a live modal.
            Assert.Equal(owned, TestCommandUiDialog.IsParsekDialogName(name));
        }

        [Fact]
        public void DialogPayload_CarriesAllSevenKeysBothWays()
        {
            var closed = TestCommandUiDialog.BuildPayload(false, 0, null, null, null);
            Assert.Equal("false", Value(closed, "open"));
            Assert.Equal("0", Value(closed, "count"));
            Assert.Equal("-", Value(closed, "name"));
            Assert.Equal("-", Value(closed, "title"));
            Assert.Equal("-", Value(closed, "buttons"));
            Assert.Equal("0", Value(closed, "nbuttons"));

            var open = TestCommandUiDialog.BuildPayload(
                true, 1, "ParsekMerge", "Merge flight?",
                new List<string> { "Merge", "Merge and Seal", "Discard" });
            Assert.Equal("true", Value(open, "open"));
            Assert.Equal("1", Value(open, "count"));
            Assert.Equal("ParsekMerge", Value(open, "name"));
            Assert.Equal("Merge flight?", Value(open, "title"));
            Assert.Equal("Merge|Merge and Seal|Discard", Value(open, "buttons"));
            Assert.Equal("3", Value(open, "nbuttons"));
        }

        [Fact]
        public void DialogPayload_ButtonSeparatorRidesTheWireRaw()
        {
            // `|` is printable ASCII and neither `%` nor `=`, the only two characters
            // TestCommandProtocol.NeedsEncoding escapes ABOVE 0x20 - so the SEPARATOR
            // survives and a reader can split the value. A comma would collide with the
            // labels themselves, which are player-facing sentences.
            //
            // The labels' own spaces are still percent-encoded (NeedsEncoding covers
            // `b <= 0x20`), which is the wire's business and not this formatter's: what
            // matters is that the separator is not, so `%20`-riddled labels still split
            // into the right number of parts.
            string formatted = TestCommandUiDialog.FormatButtons(
                new List<string> { "Yes, do it", "No" });
            Assert.Equal("Yes, do it|No", formatted);
            string encoded = TestCommandProtocol.Encode(formatted);
            Assert.Contains("|", encoded);
            Assert.Equal(2, encoded.Split('|').Length);
        }

        [Fact]
        public void DialogPayload_EmptyLabelsBecomeTheDashSentinel()
        {
            Assert.Equal("-|Discard", TestCommandUiDialog.FormatButtons(
                new List<string> { null, "Discard" }));
        }

        // ================================================================ reason vocabulary

        [Fact]
        public void EveryNewReasonTokenIsDistinct()
        {
            // A duplicated token would make two different refusals indistinguishable on the
            // wire, which is the one thing a typed-reason vocabulary exists to prevent.
            var reasons = new List<string>
            {
                TestCommandUiPointer.ArgMissingReason,
                TestCommandUiPointer.ArgInvalidReason,
                TestCommandUiPointer.ArgConflictReason,
                TestCommandUiPointer.OffScreenReason,
                TestCommandUiPointer.WindowUnresolvedReason,
                TestCommandUiPointer.UnsupportedPlatformReason,
                TestCommandUiPointer.NotAppliedReason,
                TestCommandUiFind.TextArgMissingReason,
                TestCommandUiFind.CtrlArgInvalidReason,
                TestCommandUiFind.IndexArgInvalidReason,
                TestCommandUiFind.WindowNotDrawnReason,
                TestCommandUiFind.ControlNotFoundReason,
                TestCommandUiFind.IndexOutOfRangeReason,
                TestCommandUiFind.CaptureFailedReason,
                TestCommandUiState.ExpandUnsupportedWindowReason,
                TestCommandUiState.ExpandKeyArgMissingReason,
                TestCommandUiState.ExpandKeyInvalidReason,
                TestCommandUiState.ExpandKeyUnknownReason,
                TestCommandUiState.StateArgInvalidReason,
                TestCommandUiState.ExpandStateWithBulkKeyReason,
                TestCommandUiState.TargetUnsupportedWindowReason,
                TestCommandUiState.TargetArgMissingReason,
                TestCommandUiState.TargetArgConflictReason,
                TestCommandUiState.TargetNotFoundReason,
                TestCommandUiState.TargetNotOpenedReason,
                TestCommandUiState.PickerUnsupportedWindowReason,
                TestCommandUiState.PickerArgMissingReason,
                TestCommandUiState.PickerArgConflictReason,
                TestCommandUiState.PickerTargetNotFoundReason,
                TestCommandUiState.PickerNotOpenedReason,
                TestCommandUiDialog.DialogArgInvalidReason,
            };
            Assert.Equal(reasons.Count, reasons.Distinct().Count());
            // And none collides with the original six ops' vocabulary.
            foreach (string reason in reasons)
            {
                Assert.NotEqual(TestCommandUiAction.OpArgInvalidReason, reason);
                Assert.NotEqual(TestCommandUiAction.RectNotAppliedReason, reason);
                Assert.NotEqual(TestCommandUiAction.WindowSelfClosedReason, reason);
            }
        }

        // ================================================ op=pointer: the settle POLL

        [Fact]
        public void PointerPoll_WaitsForTheReadBackInsteadOfReadingItOnce()
        {
            // THE DEFECT. The op used to settle on the shared frame poll and read
            // Input.mousePosition exactly once, one frame after SetCursorPos returned. The
            // OS cursor move and Unity's next input sample are different pipelines, so when
            // they landed a frame apart the single read saw the OLD position and the op
            // answered the hard `pointer-not-applied` ERROR over a move that was about to
            // be correct - indistinguishable, from one sample, from an unfocused game.
            Assert.Equal(UiActionSettleOutcome.NotYet,
                TestCommandUiPointer.DecidePoll(
                    landed: false, framesElapsed: 1, minFrames: 1, budgetExpired: false));
            // The very next poll, with the sample caught up, is a success.
            Assert.Equal(UiActionSettleOutcome.Settled,
                TestCommandUiPointer.DecidePoll(
                    landed: true, framesElapsed: 2, minFrames: 1, budgetExpired: false));
        }

        [Fact]
        public void PointerPoll_FramesAreAFloorAndNotTheSignal()
        {
            // A cursor that arrived before any IMGUI pass has hovered nothing yet, so the
            // frame count still gates the OK - it just is not what the op is waiting FOR.
            Assert.Equal(UiActionSettleOutcome.NotYet,
                TestCommandUiPointer.DecidePoll(
                    landed: true, framesElapsed: 0, minFrames: 1, budgetExpired: false));
            Assert.Equal(UiActionSettleOutcome.Settled,
                TestCommandUiPointer.DecidePoll(
                    landed: true, framesElapsed: 1, minFrames: 1, budgetExpired: false));
            // The floor the applier passes is the shared one, so a change there moves both.
            Assert.Equal(1, TestCommandUiAction.SettleFrames);
        }

        [Fact]
        public void PointerPoll_SettledBeatsTheBudgetOnTheSamePoll()
        {
            // The DecideSettlePoll / CaptureScreenshot.DecidePoll ordering rule: a landing
            // that happened on the very poll the budget expired is a success, not an ERROR
            // over state that is already correct.
            Assert.Equal(UiActionSettleOutcome.Settled,
                TestCommandUiPointer.DecidePoll(
                    landed: true, framesElapsed: 1, minFrames: 1, budgetExpired: true));
            // And a budget expiry with the cursor still elsewhere is the honest timeout,
            // which the applier reports as `pointer-not-applied` with the last reading.
            Assert.Equal(UiActionSettleOutcome.TimedOut,
                TestCommandUiPointer.DecidePoll(
                    landed: false, framesElapsed: 9, minFrames: 1, budgetExpired: true));
            // The frame floor does NOT hold a timeout open: a renderer that stopped would
            // otherwise leave the head pending past its budget.
            Assert.Equal(UiActionSettleOutcome.TimedOut,
                TestCommandUiPointer.DecidePoll(
                    landed: true, framesElapsed: 0, minFrames: 1, budgetExpired: true));
        }

        [Fact]
        public void PointerPoll_UsesTheSameToleranceTheReadBackDoes()
        {
            // The poll's `landed` argument is LandedWithinTolerance, not a fresh
            // comparison, so the two cannot drift. Stated by exercising the boundary the
            // tolerance names.
            Assert.True(TestCommandUiPointer.LandedWithinTolerance(
                100f, 200f, 100f + TestCommandUiPointer.ReadBackTolerancePx, 200f));
            Assert.False(TestCommandUiPointer.LandedWithinTolerance(
                100f, 200f, 100f + TestCommandUiPointer.ReadBackTolerancePx + 0.5f, 200f));
        }

        // ================================================ op=dialog: nested button walk

        [Fact]
        public void DialogButtons_AreFoundInsideNestedLayouts()
        {
            // THE DEFECT. The scan read the top level of MultiOptionDialog.options alone,
            // so a dialog that wraps its buttons in a layout - the ordinary way to put two
            // buttons on one row - reported nbuttons=0 to op=dialog over a popup that
            // plainly has buttons, and a census step would have photographed the modal and
            // asserted it has none.
            var nested = new DialogGUIBase();
            nested.children.Add(new DialogGUIButton("Merge", () => { }));
            nested.children.Add(new DialogGUIButton("Discard", () => { }));
            var options = new DialogGUIBase[] { new DialogGUIBase(), nested };

            List<DialogGUIButton> found = TestCommandUiDialog.CollectButtons(options);
            Assert.Equal(2, found.Count);
            Assert.Equal("Merge", found[0].OptionText);
            Assert.Equal("Discard", found[1].OptionText);
        }

        [Fact]
        public void DialogButtons_KeepDepthFirstLeftToRightOrder()
        {
            // AnswerMergeDialog selects BY POSITION (first = Merge, last = Discard), so the
            // order this walk produces is not cosmetic. Depth-first left-to-right is the
            // order the dialog lays the controls out in.
            var row = new DialogGUIBase();
            row.children.Add(new DialogGUIButton("b", () => { }));
            row.children.Add(new DialogGUIButton("c", () => { }));
            var options = new DialogGUIBase[]
            {
                new DialogGUIButton("a", () => { }),
                row,
                new DialogGUIButton("d", () => { }),
            };
            Assert.Equal(new[] { "a", "b", "c", "d" },
                         TestCommandUiDialog.CollectButtons(options)
                             .Select(b => b.OptionText).ToArray());
        }

        [Fact]
        public void DialogButtons_DoNotDescendIntoAButtonsOwnChildren()
        {
            // A button is a leaf CONTROL; anything under one is decoration, not a second
            // option a caller could press. Counting it would shift every by-position
            // selection after it.
            var button = new DialogGUIButton("Merge", () => { });
            button.children.Add(new DialogGUIButton("decoration", () => { }));
            List<DialogGUIButton> found =
                TestCommandUiDialog.CollectButtons(new DialogGUIBase[] { button });
            Assert.Single(found);
            Assert.Equal("Merge", found[0].OptionText);
        }

        [Fact]
        public void DialogButtons_TolerateNullsAndACyclicChildGraph()
        {
            // `children` is a mutable PUBLIC field on a stock type. The seam must not hang
            // the FIFO head over a malformed dialog, which is what the depth bound is for.
            Assert.Empty(TestCommandUiDialog.CollectButtons(null));
            var a = new DialogGUIBase();
            var b = new DialogGUIBase();
            a.children.Add(null);
            a.children.Add(b);
            b.children.Add(a);
            b.children.Add(new DialogGUIButton("reachable", () => { }));
            List<DialogGUIButton> found =
                TestCommandUiDialog.CollectButtons(new DialogGUIBase[] { a });
            Assert.Contains(found, x => x.OptionText == "reachable");
            Assert.True(found.Count <= TestCommandUiDialog.ButtonWalkMaxDepth + 1,
                "the depth bound must stop a cyclic children graph, found "
                + found.Count + " buttons");
        }

        // ================================================ AnswerMergeDialog dialog=

        [Fact]
        public void AnswerDialogArg_RejectsEveryValueOutsideTheClosedSet()
        {
            // The closed set is ONE value today, which is exactly when a bare default is
            // tempting and wrong: a typo must fail rather than silently answer the merge
            // dialog. Driven over the shapes a spec author actually produces.
            foreach (string bad in new[] { "preswitch", "Merge", "MERGE", "merge ",
                                           "", "refly", "tree-merge" })
            {
                Assert.False(TestCommandUiDialog.TryParseAnswerDialog(
                                 bad, out _, out string reason),
                             "dialog=" + bad + " must be refused");
                Assert.Equal(TestCommandUiDialog.DialogArgInvalidReason, reason);
            }
            // And the two accepted shapes: absent (the pre-arg default) and the one token.
            Assert.True(TestCommandUiDialog.TryParseAnswerDialog(null, out string t0, out _));
            Assert.Equal(TestCommandUiDialog.MergeDialogToken, t0);
            Assert.True(TestCommandUiDialog.TryParseAnswerDialog(
                TestCommandUiDialog.MergeDialogToken, out string t1, out _));
            Assert.Equal(TestCommandUiDialog.MergeDialogToken, t1);
        }

        // ================================================ logistics candidate row keys

        [Fact]
        public void LogisticsCandidateRowKey_IsTheKeyTheDrawSiteUses()
        {
            // THE DEFECT. EnumerateRowKeysForTesting listed the three fixed sections and
            // the committed routes and NOT the candidate rows, so `op=expand key=all` left
            // every candidate collapsed and `key=row:cand:<id>` was REJECTED
            // expand-key-unknown - over rows the window was drawing. The key is now built
            // at ONE site that both the draw path and the enumeration call, so the two
            // cannot drift again.
            var candidate = new RouteCandidate { Tree = new RecordingTree { Id = "t-17" } };
            Assert.Equal("cand:t-17", LogisticsWindowUI.CandidateRowKey(candidate));
        }

        [Fact]
        public void LogisticsCandidateRowKey_MatchesTheDrawSitesNullForm()
        {
            // The draw site's own fallback, character for character. A key that LOOKED
            // addressable but differed would be worse than the omission: it would toggle
            // nothing and report OK.
            Assert.Equal("cand:<no-tree>",
                         LogisticsWindowUI.CandidateRowKey(new RouteCandidate()));
            Assert.Equal("cand:<no-tree>", LogisticsWindowUI.CandidateRowKey(null));
        }

        [Fact]
        public void LogisticsCandidateRowKey_CarriesTheWindowsOwnExpandPrefix()
        {
            // The wire key is `row:` + this raw key, and `row` is the only prefix the
            // logistics window keeps - so a candidate row is addressable as
            // `key=row:cand:<treeId>` and nothing else.
            string[] prefixes = TestCommandUiState.ExpandPrefixesFor(
                TestCommandUiAction.LogisticsWindow);
            Assert.Equal(new[] { TestCommandUiState.RowKeyPrefix }, prefixes);
        }
    }
}
