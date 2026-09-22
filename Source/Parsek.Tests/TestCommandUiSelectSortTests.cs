using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the two census ops that drive a table's ORDERING and the Missions
    /// tab's INCLUDE affordances (<see cref="TestCommandUiSelectSort"/>).
    ///
    /// <para>Three properties carry the weight. First, every column token must resolve to
    /// the INDEX the window's header writes for it: the live accessors take an int, so a
    /// wrong number here sorts by a neighbouring column and nothing in the game reports it -
    /// a capture would simply show an ordering nobody asked for under the lane's label.
    /// Second, the Missions window's two tabs draw two DIFFERENT tables whose index and name
    /// columns collide, so a column resolved against the tab the window is not drawing is
    /// the same defect wearing a plausible answer; the per-tab narrowing is what makes it a
    /// typed REJECTED naming the live tab. Third, both direction args are REQUIRED and a
    /// bulk key REFUSES one, because a defaulted or overridden direction silently runs the
    /// opposite half of a lane's check.</para>
    /// </summary>
    public class TestCommandUiSelectSortTests
    {
        // ================================================================ op=sort

        // ----- the sortable-window predicate -----

        [Theory]
        [InlineData("missions", true)]
        [InlineData("logistics", true)]
        [InlineData("spawncontrol", true)]
        [InlineData("timeline", false)]
        [InlineData("kerbals", false)]
        [InlineData("career", false)]
        [InlineData("main", false)]
        [InlineData("settings", false)]
        [InlineData("structure", false)]
        [InlineData("gloops", false)]
        [InlineData("testrunner", false)]
        [InlineData("testrunnerglobal", false)]
        public void SortableWindows_AreExactlyTheThreeWithATable(string window,
                                                                 bool sortable)
        {
            Assert.Equal(sortable,
                TestCommandUiSelectSort.WindowHasSortableTable(window));
        }

        [Fact]
        public void SortableWindowNames_ListsTheThree()
        {
            Assert.Equal("missions,logistics,spawncontrol",
                TestCommandUiSelectSort.SortableWindowNames);
        }

        [Fact]
        public void OnlyTheMissionsWindow_ScopesItsColumnsByTab()
        {
            Assert.True(TestCommandUiSelectSort.SortIsTabScoped("missions"));
            Assert.False(TestCommandUiSelectSort.SortIsTabScoped("logistics"));
            Assert.False(TestCommandUiSelectSort.SortIsTabScoped("spawncontrol"));
        }

        [Fact]
        public void AnUnsortableWindow_IsItsOwnReject()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "timeline", null, "name", out int index, out string reason));
            Assert.Equal(-1, index);
            Assert.Equal(TestCommandUiSelectSort.SortUnsupportedWindowReason, reason);
        }

        // ----- the column tables: every token resolves to the header's own index -----
        //
        // The expected numbers are the enum members' own, written as literals for the reason
        // the production table writes them as literals: the enums are the window classes' own
        // and this half must stay free of window references. A drift between the two is
        // exactly what this cell is for.

        [Theory]
        // MissionsWindowUI.MissionSortColumn {Index, Name, StartTime}
        [InlineData("missions", "index", 0)]
        [InlineData("missions", "name", 1)]
        [InlineData("missions", "start", 2)]
        public void MissionsTabColumns_ResolveToTheirEnumIndex(string tab, string column,
                                                               int expected)
        {
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", tab, column, out int index, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, index);
        }

        [Theory]
        // RecordingsTableUI.SortColumn {Index, Phase, Name, LaunchTime, Duration, Status,
        // LaunchSite} - DECLARATION order, which is not the header row's left-to-right order
        // (Site draws between Name and Launch and is the enum's last member).
        [InlineData("index", 0)]
        [InlineData("phase", 1)]
        [InlineData("name", 2)]
        [InlineData("launch", 3)]
        [InlineData("duration", 4)]
        [InlineData("status", 5)]
        [InlineData("site", 6)]
        public void RecordingsTabColumns_ResolveToTheirEnumIndex(string column, int expected)
        {
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "recordings", column, out int index, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, index);
        }

        [Theory]
        // LogisticsRouteSortColumn, whose members carry EXPLICIT numbers.
        [InlineData("name", 0)]
        [InlineData("origin", 1)]
        [InlineData("destination", 2)]
        [InlineData("interval", 3)]
        [InlineData("cycles", 4)]
        [InlineData("next", 5)]
        [InlineData("status", 6)]
        [InlineData("delivery", 7)]
        public void LogisticsColumns_ResolveToTheirEnumIndex(string column, int expected)
        {
            // No tab: the Logistics window has no selector, and its ONE sort state drives
            // both route tables exactly as the header click does.
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "logistics", null, column, out int index, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, index);
        }

        [Theory]
        // SpawnControlSortColumn {Name, Distance, RelativeSpeed, SpawnTime}
        [InlineData("craft", 0)]
        [InlineData("dist", 1)]
        [InlineData("relspeed", 2)]
        [InlineData("spawntime", 3)]
        public void SpawnControlColumns_ResolveToTheirEnumIndex(string column, int expected)
        {
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "spawncontrol", null, column, out int index, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, index);
        }

        /// <summary>
        /// The Spawn Control table draws FIVE sortable header cells over FOUR sort columns:
        /// "Spawns at" and "In T-" both write <c>SpawnControlSortColumn.SpawnTime</c>. So the
        /// wire carries ONE token for the two cells - a second token would resolve to the
        /// same index and let two spec steps claim different columns while the window sorted
        /// by one.
        /// </summary>
        [Fact]
        public void SpawnControl_HasOneTokenForTheTwoSpawnTimeCells()
        {
            string names = TestCommandUiSelectSort.SortColumnNamesFor("spawncontrol", null);
            Assert.Equal("craft,dist,relspeed,spawntime", names);
            Assert.Single(names.Split(',').Where(n => n == "spawntime"));
        }

        [Fact]
        public void EveryColumnTable_HasDistinctTokensAndDistinctIndexes()
        {
            AssertTableIsInjective("missions", "missions");
            AssertTableIsInjective("missions", "recordings");
            AssertTableIsInjective("logistics", null);
            AssertTableIsInjective("spawncontrol", null);
        }

        private static void AssertTableIsInjective(string window, string tab)
        {
            UiSortColumnSpec[] cols = TestCommandUiSelectSort.SortColumnsFor(window, tab);
            Assert.NotNull(cols);
            Assert.Equal(cols.Length, cols.Select(c => c.Token).Distinct().Count());
            Assert.Equal(cols.Length, cols.Select(c => c.EnumIndex).Distinct().Count());
        }

        // ----- the per-tab narrowing -----

        [Fact]
        public void ARecordingsTabColumn_IsRefusedWhileTheMissionsTabIsLive()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "missions", "duration", out int index, out string reason));
            Assert.Equal(-1, index);
            Assert.Equal(TestCommandUiSelectSort.SortColumnNotOnTabReason, reason);
        }

        [Fact]
        public void AMissionsTabColumn_IsRefusedWhileTheRecordingsTabIsLive()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "recordings", "start", out int index, out string reason));
            Assert.Equal(-1, index);
            Assert.Equal(TestCommandUiSelectSort.SortColumnNotOnTabReason, reason);
        }

        /// <summary>The two COLLIDING tokens are the point of the narrowing: both tabs draw
        /// an index and a name column, and they mean different enums, so each must resolve to
        /// its own tab's number rather than to one shared answer.</summary>
        [Fact]
        public void TheCollidingTokens_ResolveDifferentlyPerTab()
        {
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "missions", "name", out int missionsName, out string _));
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "recordings", "name", out int recordingsName, out string _));
            Assert.Equal(1, missionsName);
            Assert.Equal(2, recordingsName);

            // `index` is 0 on BOTH - the same number for two different enums, which is why
            // the narrowing cannot be replaced by a "the indexes agree" shortcut.
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "missions", "index", out int missionsIndex, out string _));
            Assert.True(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "recordings", "index", out int recordingsIndex, out string _));
            Assert.Equal(0, missionsIndex);
            Assert.Equal(0, recordingsIndex);
        }

        [Fact]
        public void AColumnNoTabDraws_IsTheInvalidRejectNotTheWrongTabOne()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "missions", "missions", "nosuchcolumn", out int _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortColumnInvalidReason, reason);
            Assert.False(TestCommandUiSelectSort.IsColumnOnAnotherTab(
                "missions", "missions", "nosuchcolumn"));
        }

        [Fact]
        public void AWindowWithoutTabs_NeverReportsTheWrongTabReason()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "logistics", null, "duration", out int _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortColumnInvalidReason, reason);
            Assert.False(TestCommandUiSelectSort.IsColumnOnAnotherTab(
                "logistics", null, "duration"));
        }

        [Fact]
        public void AnUnmodelledLiveTab_IsFailClosedRatherThanDefaultedToTheFirstTab()
        {
            // TabTokenAt answers null for an index the window table does not model. Resolving
            // against the first tab anyway would sort by a column of a table the window is
            // not drawing.
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "missions", null, "name", out int _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortTabUnresolvedReason, reason);
        }

        [Fact]
        public void AMissingColumnArg_IsItsOwnReject()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "logistics", null, null, out int _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortColumnArgMissingReason, reason);

            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "logistics", null, "", out int _, out string emptyReason));
            Assert.Equal(TestCommandUiSelectSort.SortColumnArgMissingReason, emptyReason);
        }

        [Fact]
        public void ColumnResolution_IsCaseSensitive()
        {
            Assert.False(TestCommandUiSelectSort.TryResolveColumn(
                "logistics", null, "Name", out int _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortColumnInvalidReason, reason);
        }

        // ----- the direction arg -----

        [Fact]
        public void AMissingDir_IsRejectedRatherThanDefaulted()
        {
            Assert.False(TestCommandUiSelectSort.TryParseDirection(
                null, out bool ascending, out string reason));
            Assert.True(ascending);
            Assert.Equal(TestCommandUiSelectSort.SortDirArgMissingReason, reason);
        }

        [Theory]
        [InlineData("asc", true)]
        [InlineData("desc", false)]
        public void TheTwoDirections_Parse(string raw, bool expected)
        {
            Assert.True(TestCommandUiSelectSort.TryParseDirection(
                raw, out bool ascending, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, ascending);
            Assert.Equal(raw, TestCommandUiSelectSort.DirToken(ascending));
        }

        [Theory]
        [InlineData("ASC")]
        [InlineData("ascending")]
        [InlineData("true")]
        [InlineData("")]
        public void AnyOtherDir_IsInvalid(string raw)
        {
            Assert.False(TestCommandUiSelectSort.TryParseDirection(
                raw, out bool _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SortDirArgInvalidReason, reason);
        }

        [Fact]
        public void DirTokenNames_CarriesBoth()
        {
            Assert.Equal("asc,desc", TestCommandUiSelectSort.DirTokenNames);
        }

        // ----- the sort payload -----

        [Fact]
        public void SortPayload_CarriesTheSixFieldsInOrder()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiSelectSort.BuildSortPayload(
                    "missions", "recordings", "duration", ascending: false, changed: true);
            Assert.Equal(new[] { "op", "window", "tab", "column", "dir", "changed" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(new[] { "sort", "missions", "recordings", "duration", "desc", "true" },
                payload.Select(kv => kv.Value).ToArray());
        }

        [Fact]
        public void SortPayload_ReportsNoTabAsTheSentinel()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiSelectSort.BuildSortPayload(
                    "logistics", null, "name", ascending: true, changed: false);
            Assert.Equal("-", payload.Single(kv => kv.Key == "tab").Value);
            Assert.Equal("asc", payload.Single(kv => kv.Key == "dir").Value);
            Assert.Equal("false", payload.Single(kv => kv.Key == "changed").Value);
        }

        [Fact]
        public void TabField_MapsBothNullAndEmptyToTheSentinel()
        {
            Assert.Equal("-", TestCommandUiSelectSort.TabField(null));
            Assert.Equal("-", TestCommandUiSelectSort.TabField(""));
            Assert.Equal("missions", TestCommandUiSelectSort.TabField("missions"));
        }

        // ================================================================ op=select

        // ----- the window gate -----

        [Theory]
        [InlineData("missions", true)]
        [InlineData("logistics", false)]
        [InlineData("timeline", false)]
        [InlineData("kerbals", false)]
        public void SelectIsDefinedForTheMissionsWindowOnly(string window, bool supported)
        {
            Assert.Equal(supported,
                TestCommandUiSelectSort.WindowHasSelectAffordance(window));
        }

        [Fact]
        public void SelectAgainstAnotherWindow_IsItsOwnReject()
        {
            Assert.False(TestCommandUiSelectSort.TryParseSelectKey(
                "logistics", "vessel:rec1", includeGiven: true,
                out UiSelectScope _, out string _, out string _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SelectUnsupportedWindowReason, reason);
        }

        [Fact]
        public void SelectableWindowNames_IsTheOneWindow()
        {
            Assert.Equal("missions", TestCommandUiSelectSort.SelectableWindowNames);
        }

        // ----- the key grammar -----

        [Theory]
        [InlineData("vessel:rec17", "vessel", "rec17")]
        [InlineData("link:bp-guid-1", "link", "bp-guid-1")]
        public void ASingleKey_SplitsAtItsPrefix(string raw, string prefix, string value)
        {
            Assert.True(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", raw, includeGiven: true,
                out UiSelectScope scope, out string parsedPrefix, out string parsedValue,
                out string reason));
            Assert.Null(reason);
            Assert.Equal(UiSelectScope.Single, scope);
            Assert.Equal(prefix, parsedPrefix);
            Assert.Equal(value, parsedValue);
        }

        /// <summary>The split is at the FIRST colon and the value half keeps the rest, which
        /// is what lets a lane name a key that carries one - an <c>op=expand</c>-shaped
        /// <c>missionId:headId</c>, or any future inner-colon interval key.</summary>
        [Fact]
        public void AKeyWithAnInnerColon_KeepsItsWholeValueHalf()
        {
            Assert.True(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", "vessel:m17:head4", includeGiven: true,
                out UiSelectScope _, out string prefix, out string value, out string reason));
            Assert.Null(reason);
            Assert.Equal("vessel", prefix);
            Assert.Equal("m17:head4", value);
        }

        [Theory]
        [InlineData("vessel")]          // no colon at all
        [InlineData(":rec17")]          // empty prefix
        [InlineData("vessel:")]         // empty value
        [InlineData("group:Boosters")]  // a prefix op=expand keeps and this op does not
        [InlineData("leg:m1:seg0")]
        public void AMalformedOrForeignKey_IsTheInvalidReject(string raw)
        {
            Assert.False(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", raw, includeGiven: true,
                out UiSelectScope _, out string _, out string _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SelectKeyInvalidReason, reason);
        }

        [Fact]
        public void AMissingKey_IsItsOwnReject()
        {
            Assert.False(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", null, includeGiven: true,
                out UiSelectScope _, out string _, out string _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SelectKeyArgMissingReason, reason);
        }

        [Fact]
        public void SelectPrefixNames_CarriesBoth()
        {
            Assert.Equal("vessel,link", TestCommandUiSelectSort.SelectPrefixNames);
        }

        /// <summary>The vessel prefix is the SAME token <c>op=expand</c> uses for the
        /// Missions window's vessel rows, reused rather than re-spelled: two spellings for
        /// one collection is how a spec author picks the wrong one.</summary>
        [Fact]
        public void TheVesselPrefix_IsTheOneExpandAlreadyUses()
        {
            Assert.Equal(TestCommandUiState.VesselKeyPrefix,
                TestCommandUiSelectSort.VesselKeyPrefix);
        }

        // ----- the bulk keys carry their own direction -----

        [Theory]
        [InlineData("all", 1, true)]
        [InlineData("none", 2, false)]
        public void ABulkKey_ParsesAndCarriesItsDirection(string raw, int scopeValue,
                                                           bool includes)
        {
            Assert.True(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", raw, includeGiven: false,
                out UiSelectScope scope, out string prefix, out string value,
                out string reason));
            Assert.Null(reason);
            Assert.Equal(scopeValue, (int)scope);
            Assert.Null(prefix);
            Assert.Null(value);
            Assert.Equal(includes, TestCommandUiSelectSort.BulkScopeIncludes(scope));
        }

        [Theory]
        [InlineData("all")]
        [InlineData("none")]
        public void ABulkKeyBesideAnInclude_IsRefusedRatherThanOverridden(string raw)
        {
            Assert.False(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", raw, includeGiven: true,
                out UiSelectScope _, out string _, out string _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SelectIncludeWithBulkKeyReason, reason);
        }

        [Fact]
        public void TheBulkTokens_AreTheOnesExpandAlreadyUses()
        {
            Assert.True(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", TestCommandUiState.ExpandAllToken, includeGiven: false,
                out UiSelectScope all, out string _, out string _, out string _));
            Assert.Equal(UiSelectScope.All, all);
            Assert.True(TestCommandUiSelectSort.TryParseSelectKey(
                "missions", TestCommandUiState.ExpandNoneToken, includeGiven: false,
                out UiSelectScope none, out string _, out string _, out string _));
            Assert.Equal(UiSelectScope.None, none);
        }

        // ----- the include arg -----

        [Fact]
        public void AMissingInclude_IsRejectedRatherThanDefaulted()
        {
            Assert.False(TestCommandUiSelectSort.TryParseInclude(
                null, out bool include, out string reason));
            Assert.False(include);
            Assert.Equal(TestCommandUiSelectSort.SelectIncludeArgMissingReason, reason);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void TheTwoIncludeValues_Parse(string raw, bool expected)
        {
            Assert.True(TestCommandUiSelectSort.TryParseInclude(
                raw, out bool include, out string reason));
            Assert.Null(reason);
            Assert.Equal(expected, include);
            Assert.Equal(raw, TestCommandUiSelectSort.IncludeToken(include));
        }

        [Theory]
        [InlineData("True")]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("")]
        public void AnyOtherInclude_IsInvalid(string raw)
        {
            Assert.False(TestCommandUiSelectSort.TryParseInclude(
                raw, out bool _, out string reason));
            Assert.Equal(TestCommandUiSelectSort.SelectIncludeArgInvalidReason, reason);
        }

        /// <summary><c>include=</c> speaks the ONE <c>state=</c> vocabulary the rest of the
        /// seam uses, so a spec author does not learn a second pair of words for the same
        /// two values.</summary>
        [Fact]
        public void IncludeSpeaksTheSharedTrueFalseVocabulary()
        {
            Assert.Equal(TestCommandUiState.StateTrueToken,
                TestCommandUiSelectSort.IncludeToken(true));
            Assert.Equal(TestCommandUiState.StateFalseToken,
                TestCommandUiSelectSort.IncludeToken(false));
        }

        // ----- the select payload -----

        [Fact]
        public void SelectPayload_CarriesTheEightFieldsInOrder()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiSelectSort.BuildSelectPayload(
                    "missions", "m-17", "vessel:rec4", include: false, changed: 3,
                    excluded: 5, links: 1);
            Assert.Equal(
                new[] { "op", "window", "mission", "key", "include", "changed", "excluded",
                        "links" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(
                new[] { "select", "missions", "m-17", "vessel:rec4", "false", "3", "5", "1" },
                payload.Select(kv => kv.Value).ToArray());
        }

        /// <summary>The three numbers are invariant-culture: the harness parses them, and the
        /// xUnit host runs under the OS culture.</summary>
        [Fact]
        public void SelectPayloadNumbers_AreInvariant()
        {
            using (new CultureSwap("de-DE"))
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandUiSelectSort.BuildSelectPayload(
                        "missions", "m-1", "all", include: true, changed: 1234,
                        excluded: 2345, links: 3456);
                Assert.Equal("1234", payload.Single(kv => kv.Key == "changed").Value);
                Assert.Equal("2345", payload.Single(kv => kv.Key == "excluded").Value);
                Assert.Equal("3456", payload.Single(kv => kv.Key == "links").Value);
            }
        }

        [Fact]
        public void SelectPayload_TreatsANullMissionAndKeyAsEmptyRatherThanThrowing()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiSelectSort.BuildSelectPayload(
                    "missions", null, null, include: true, changed: 0, excluded: 0,
                    links: 0);
            Assert.Equal("", payload.Single(kv => kv.Key == "mission").Value);
            Assert.Equal("", payload.Single(kv => kv.Key == "key").Value);
        }

        /// <summary>Scoped OS-culture swap: the invariance cell above must prove the
        /// production site is invariant, not that the host happens to be.</summary>
        private sealed class CultureSwap : System.IDisposable
        {
            private readonly System.Globalization.CultureInfo previous;

            internal CultureSwap(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo(name);
            }

            public void Dispose()
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
