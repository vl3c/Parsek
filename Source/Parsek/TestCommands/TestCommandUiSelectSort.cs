using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>One driveable sort column: its wire token and the INDEX the window's own
    /// sort-column field takes.</summary>
    internal struct UiSortColumnSpec
    {
        internal string Token;

        /// <summary>The window enum member's own number, as the
        /// <c>SortColumnIndexForTesting</c> accessors take it. An int rather than the enum
        /// for the reason those accessors are ints: the enums are the window classes' own,
        /// and this half must stay free of window / Unity references so xUnit can exercise
        /// it without KSP.</summary>
        internal int EnumIndex;
    }

    /// <summary>Whether an <c>op=select</c> call names one key or the whole mission.</summary>
    internal enum UiSelectScope
    {
        /// <summary>One <c>&lt;prefix&gt;:&lt;value&gt;</c> key.</summary>
        Single = 0,

        /// <summary><c>key=all</c>: include every vessel and every partner journey.</summary>
        All = 1,

        /// <summary><c>key=none</c>: exclude every vessel and every partner journey.</summary>
        None = 2,
    }

    /// <summary>
    /// Pure decision / payload half of the two automation-only ops that drive a TABLE's
    /// ordering (<c>op=sort</c>) and the Missions tab's INCLUDE affordances
    /// (<c>op=select</c>).
    ///
    /// <para><b>WHY THE TWO SHARE A FILE.</b> Nothing but the census wave: they are the
    /// remaining two shapes of driveable Missions-window state, and they are each a parse
    /// plus a payload over a handful of live fields. They share no vocabulary - <c>sort</c>
    /// speaks <c>column=</c> / <c>dir=</c> and <c>select</c> speaks <c>key=</c> /
    /// <c>include=</c> - and the two halves below are independent.</para>
    ///
    /// <para><b><c>op=select</c> WRITES STATE THAT SURVIVES THE SAVE.</b>
    /// <c>Mission.ExcludedIntervalKeys</c> and <c>Mission.IncludedForeignDockLinkIds</c> are
    /// serialized by the Mission codec, so a lane that drives this op has EDITED THE
    /// PLAYER'S CAREER and left the edit behind. Every op beside it in this family writes
    /// view state that dies with the process; this one does not. A census lane using it
    /// therefore runs on a THROWAWAY STAGED SAVE - and that is a LANE RULE, not something
    /// this op can enforce: nothing in the seam can tell a staged fixture from a real
    /// career, and refusing on a heuristic would refuse the legitimate lane. The one thing
    /// the op does do about it is report the standing counts
    /// (<c>excluded=</c> / <c>links=</c>) so the edit is visible in the answer rather than
    /// only on disk.</para>
    ///
    /// <para><b>BOTH DIRECTION ARGS ARE REQUIRED.</b> <c>dir=</c> and <c>include=</c> have
    /// no default, the <c>op=playback</c> <c>state=</c> rationale verbatim: a census drives
    /// each of them BOTH ways (ascending is one picture and descending is another; an
    /// excluded vessel greys its row and an included one does not), so guessing a direction
    /// would silently run the opposite half of a lane's check under the lane's own
    /// label.</para>
    /// </summary>
    internal static class TestCommandUiSelectSort
    {
        // ================================================================ op=sort

        // ----- arg keys -----

        /// <summary><c>op=sort</c>'s REQUIRED column token. CLOSED and PER-WINDOW (and, on
        /// the Missions window, per TAB): a spec names the column in a step and a capture
        /// label is built from it, so an unknown column must be a typed REJECTED carrying
        /// that table's own list rather than a silent no-op photographing the previous
        /// ordering under a label claiming the new one.</summary>
        internal const string ColumnArg = "column";

        /// <summary><c>op=sort</c>'s REQUIRED direction. Closed: <c>asc</c> |
        /// <c>desc</c>.</summary>
        internal const string DirArg = "dir";

        internal const string DirAscToken = "asc";
        internal const string DirDescToken = "desc";

        /// <summary>The two direction tokens, comma-joined for the reject message.</summary>
        internal static string DirTokenNames => DirAscToken + "," + DirDescToken;

        // ----- column tokens -----
        //
        // The tokens are the header labels lower-cased into ONE word, so a spec author reads
        // the token off the screen. Each row's comment names the enum member its index is,
        // because the index is the only thing the live accessor takes and a wrong number here
        // would sort by a neighbouring column with nothing to witness it.

        internal const string ColumnIndexToken = "index";
        internal const string ColumnNameToken = "name";
        internal const string ColumnStartToken = "start";
        internal const string ColumnPhaseToken = "phase";
        internal const string ColumnSiteToken = "site";
        internal const string ColumnLaunchToken = "launch";
        internal const string ColumnDurationToken = "duration";
        internal const string ColumnStatusToken = "status";
        internal const string ColumnOriginToken = "origin";
        internal const string ColumnDestinationToken = "destination";
        internal const string ColumnIntervalToken = "interval";
        internal const string ColumnCyclesToken = "cycles";
        internal const string ColumnNextToken = "next";
        internal const string ColumnDeliveryToken = "delivery";
        internal const string ColumnCraftToken = "craft";
        internal const string ColumnDistToken = "dist";
        internal const string ColumnRelSpeedToken = "relspeed";
        internal const string ColumnSpawnTimeToken = "spawntime";

        // The Missions window's own tab tokens, as its row of the window table spells them
        // (NewSpec(MissionsWindow, true, true, "missions", "recordings")). Named here rather
        // than re-derived so the per-tab narrowing below reads against one spelling.
        internal const string MissionsTabToken = "missions";
        internal const string RecordingsTabToken = "recordings";

        // MissionsWindowUI.MissionSortColumn {Index, Name, StartTime}. Headers: "#",
        // "Missions and vessels", "Start time".
        private static readonly UiSortColumnSpec[] MissionsTabColumns = new[]
        {
            NewColumn(ColumnIndexToken, 0),  // MissionSortColumn.Index      ("#")
            NewColumn(ColumnNameToken, 1),   // MissionSortColumn.Name       ("Missions and vessels")
            NewColumn(ColumnStartToken, 2),  // MissionSortColumn.StartTime  ("Start time")
        };

        // RecordingsTableUI.SortColumn {Index, Phase, Name, LaunchTime, Duration, Status,
        // LaunchSite}. Headers: "#", "Phase", "Name", "Launch", "Duration", "Status",
        // "Site". DECLARATION ORDER, not header order - the header row draws Site between
        // Name and Launch while the enum puts LaunchSite last, and the index is what the
        // accessor takes.
        private static readonly UiSortColumnSpec[] RecordingsTabColumns = new[]
        {
            NewColumn(ColumnIndexToken, 0),     // SortColumn.Index      ("#")
            NewColumn(ColumnPhaseToken, 1),     // SortColumn.Phase      ("Phase")
            NewColumn(ColumnNameToken, 2),      // SortColumn.Name       ("Name")
            NewColumn(ColumnLaunchToken, 3),    // SortColumn.LaunchTime ("Launch")
            NewColumn(ColumnDurationToken, 4),  // SortColumn.Duration   ("Duration")
            NewColumn(ColumnStatusToken, 5),    // SortColumn.Status     ("Status")
            NewColumn(ColumnSiteToken, 6),      // SortColumn.LaunchSite ("Site")
        };

        // LogisticsRouteSortColumn, whose members carry EXPLICIT numbers (Name = 0 ..
        // Delivery = 7). ONE sort state drives BOTH route tables (Active and Paused), which
        // is what the header click does too, so there is no per-table token.
        private static readonly UiSortColumnSpec[] LogisticsColumns = new[]
        {
            NewColumn(ColumnNameToken, 0),         // Name         ("Name")
            NewColumn(ColumnOriginToken, 1),       // Origin       ("Origin")
            NewColumn(ColumnDestinationToken, 2),  // Destination  ("Destination")
            NewColumn(ColumnIntervalToken, 3),     // Interval     ("Interval")
            NewColumn(ColumnCyclesToken, 4),       // Cycles       ("Cycle")
            NewColumn(ColumnNextToken, 5),         // NextDelivery ("Next")
            NewColumn(ColumnStatusToken, 6),       // Status       ("Status")
            NewColumn(ColumnDeliveryToken, 7),     // Delivery     ("Delivery")
        };

        // SpawnControlSortColumn {Name, Distance, RelativeSpeed, SpawnTime}.
        //
        // FOUR TOKENS FOR FIVE HEADER CELLS, and that is the table and not an omission: the
        // candidate table draws BOTH "Spawns at" and "In T-" as sortable headers and BOTH
        // write SpawnControlSortColumn.SpawnTime (the countdown is the same instant in a
        // different presentation). So `spawntime` is the one token for the two cells; a
        // second token would have to resolve to the same index and would then let two spec
        // steps claim to sort by different columns while the window sorted by one.
        private static readonly UiSortColumnSpec[] SpawnControlColumns = new[]
        {
            NewColumn(ColumnCraftToken, 0),      // Name          ("Craft")
            NewColumn(ColumnDistToken, 1),       // Distance      ("Dist")
            NewColumn(ColumnRelSpeedToken, 2),   // RelativeSpeed ("Rel Speed")
            NewColumn(ColumnSpawnTimeToken, 3),  // SpawnTime     ("Spawns at" AND "In T-")
        };

        private static UiSortColumnSpec NewColumn(string token, int enumIndex)
            => new UiSortColumnSpec { Token = token, EnumIndex = enumIndex };

        /// <summary>The windows with a sortable table, comma-joined for the reject
        /// message.</summary>
        internal static string SortableWindowNames =>
            TestCommandUiAction.MissionsWindow + ","
            + TestCommandUiAction.LogisticsWindow + ","
            + TestCommandUiAction.SpawnControlWindow;

        /// <summary>Whether <c>op=sort</c> is defined for this window.</summary>
        internal static bool WindowHasSortableTable(string window)
            => window == TestCommandUiAction.MissionsWindow
               || window == TestCommandUiAction.LogisticsWindow
               || window == TestCommandUiAction.SpawnControlWindow;

        /// <summary>Whether the window's column vocabulary depends on its live tab. Only the
        /// Missions window: its two tabs are two different tables whose index and name
        /// columns COLLIDE.</summary>
        internal static bool SortIsTabScoped(string window)
            => window == TestCommandUiAction.MissionsWindow;

        /// <summary>
        /// The columns a window accepts for a given live tab token, or null when the window
        /// keeps no sortable table (or the token does not name one of its tabs).
        /// </summary>
        internal static UiSortColumnSpec[] SortColumnsFor(string window, string tabToken)
        {
            if (window == TestCommandUiAction.LogisticsWindow) return LogisticsColumns;
            if (window == TestCommandUiAction.SpawnControlWindow) return SpawnControlColumns;
            if (window != TestCommandUiAction.MissionsWindow) return null;
            if (string.Equals(tabToken, MissionsTabToken, StringComparison.Ordinal))
                return MissionsTabColumns;
            if (string.Equals(tabToken, RecordingsTabToken, StringComparison.Ordinal))
                return RecordingsTabColumns;
            return null;
        }

        /// <summary>One table's column tokens, comma-joined, for the reject
        /// message.</summary>
        internal static string SortColumnNamesFor(string window, string tabToken)
        {
            UiSortColumnSpec[] cols = SortColumnsFor(window, tabToken);
            if (cols == null) return string.Empty;
            var names = new List<string>(cols.Length);
            for (int i = 0; i < cols.Length; i++) names.Add(cols[i].Token);
            return string.Join(",", names.ToArray());
        }

        // ----- sort: reject reasons -----

        /// <summary><c>op=sort</c> against a window with no sortable table. The message names
        /// the three that have one.</summary>
        internal const string SortUnsupportedWindowReason = "sort-unsupported-window";

        /// <summary>No <c>column=</c>. REQUIRED, never defaulted: there is no "the" column of
        /// a table, and guessing one would re-order the rows the lane never named.</summary>
        internal const string SortColumnArgMissingReason = "sort-column-arg-missing";

        /// <summary>A <c>column=</c> no tab of that window draws. The message carries the
        /// live tab's own column list.</summary>
        internal const string SortColumnInvalidReason = "sort-column-invalid";

        /// <summary>
        /// A <c>column=</c> the window HAS, on its OTHER tab. Distinct from
        /// <see cref="SortColumnInvalidReason"/> for the <c>window-has-no-tabs</c> reason:
        /// "that column does not exist" and "that column is on the tab you are not looking
        /// at" send an author to different fixes - a spelling, versus an <c>op=tab</c> step
        /// before the sort - and the message names the live tab so the remedy is readable
        /// from the answer.
        /// </summary>
        internal const string SortColumnNotOnTabReason = "sort-column-not-on-tab";

        /// <summary>No <c>dir=</c>. REQUIRED - see the class header.</summary>
        internal const string SortDirArgMissingReason = "sort-dir-arg-missing";

        /// <summary>A <c>dir=</c> outside {asc,desc}.</summary>
        internal const string SortDirArgInvalidReason = "sort-dir-arg-invalid";

        /// <summary>The window's live tab selector holds an index the window table does not
        /// model, so which of its two column vocabularies applies is unknown. Fail-closed
        /// rather than defaulting to the first tab: defaulting would resolve a column
        /// against a table the window is not drawing.</summary>
        internal const string SortTabUnresolvedReason = "sort-tab-unresolved";

        /// <summary>POST-SETTLE terminal: both fields were written and, after a drawn frame,
        /// the live pair disagrees. ERROR - we acted and the game did not follow.</summary>
        internal const string SortNotAppliedReason = "sort-not-applied";

        /// <summary>
        /// A Recordings-tab column that only draws while the tab's Info toggle is open
        /// (<c>phase</c>, <c>site</c>), asked for while Info is shut. REJECTED pre-write rather
        /// than applied: the table would sort by a column no header shows, and the tab's own
        /// Info collapse resets exactly that state, so applying it would photograph a state no
        /// click can produce. The remedy is an <c>op=state key=expandedStats state=true</c>
        /// step before the sort.
        /// </summary>
        internal const string SortColumnHiddenReason = "sort-column-hidden";

        /// <summary>
        /// Whether a resolved sort column is hidden right now: the Recordings tab's Info-only
        /// columns while Info is shut. Every other window and column answers false. Pure.
        /// </summary>
        internal static bool IsSortColumnHidden(string window, string tabToken, string columnToken,
                                                bool recordingsInfoOpen)
        {
            if (recordingsInfoOpen) return false;
            if (window != TestCommandUiAction.MissionsWindow) return false;
            if (!string.Equals(tabToken, RecordingsTabToken, StringComparison.Ordinal)) return false;
            return string.Equals(columnToken, ColumnPhaseToken, StringComparison.Ordinal)
                || string.Equals(columnToken, ColumnSiteToken, StringComparison.Ordinal);
        }

        // ----- sort: parses -----

        /// <summary>
        /// Resolves a <c>column=</c> against the table the window is CURRENTLY drawing.
        /// </summary>
        /// <param name="tabToken">The window's live tab token, or null for a window with no
        /// selector.</param>
        /// <param name="enumIndex">The number the window's own sort-column field takes.</param>
        internal static bool TryResolveColumn(string window, string tabToken, string raw,
                                              out int enumIndex, out string rejectReason)
        {
            enumIndex = -1;
            if (!WindowHasSortableTable(window))
            {
                rejectReason = SortUnsupportedWindowReason;
                return false;
            }
            UiSortColumnSpec[] cols = SortColumnsFor(window, tabToken);
            if (cols == null)
            {
                // Reachable only on the tab-scoped window with an unmodelled live index.
                rejectReason = SortTabUnresolvedReason;
                return false;
            }
            if (string.IsNullOrEmpty(raw))
            {
                rejectReason = SortColumnArgMissingReason;
                return false;
            }
            for (int i = 0; i < cols.Length; i++)
            {
                if (!string.Equals(cols[i].Token, raw, StringComparison.Ordinal)) continue;
                enumIndex = cols[i].EnumIndex;
                rejectReason = null;
                return true;
            }
            rejectReason = IsColumnOnAnotherTab(window, tabToken, raw)
                ? SortColumnNotOnTabReason
                : SortColumnInvalidReason;
            return false;
        }

        /// <summary>Whether a token the live tab does not draw belongs to the window's OTHER
        /// tab. Only the Missions window can answer true.</summary>
        internal static bool IsColumnOnAnotherTab(string window, string tabToken, string raw)
        {
            if (!SortIsTabScoped(window) || string.IsNullOrEmpty(raw)) return false;
            string other =
                string.Equals(tabToken, RecordingsTabToken, StringComparison.Ordinal)
                    ? MissionsTabToken
                    : RecordingsTabToken;
            UiSortColumnSpec[] cols = SortColumnsFor(window, other);
            if (cols == null) return false;
            for (int i = 0; i < cols.Length; i++)
                if (string.Equals(cols[i].Token, raw, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Parses the required <c>dir=</c>.</summary>
        internal static bool TryParseDirection(string raw, out bool ascending,
                                               out string rejectReason)
        {
            ascending = true;
            if (raw == null)
            {
                rejectReason = SortDirArgMissingReason;
                return false;
            }
            if (raw == DirAscToken) { rejectReason = null; return true; }
            if (raw == DirDescToken) { ascending = false; rejectReason = null; return true; }
            rejectReason = SortDirArgInvalidReason;
            return false;
        }

        /// <summary>The wire spelling of a direction, shared by the payload and the log line
        /// so a reader greps one token.</summary>
        internal static string DirToken(bool ascending)
            => ascending ? DirAscToken : DirDescToken;

        /// <summary>The <c>tab=</c> field's value for a window with no selector. A sentinel
        /// rather than an empty value, the describe payload's <c>-</c> rule: a trailing
        /// <c>tab=</c> on the wire reads as a truncated line.</summary>
        internal const string NoTabToken = "-";

        /// <summary>The <c>tab=</c> field's value for a window: its live token, or the
        /// sentinel.</summary>
        internal static string TabField(string tabToken)
            => string.IsNullOrEmpty(tabToken) ? NoTabToken : tabToken;

        // ----- sort: payload -----

        /// <summary>
        /// OK payload for <c>sort</c>:
        /// <c>op=sort window= tab= column= dir= changed=</c>.
        ///
        /// <para><c>tab</c> is reported because on the Missions window it SCOPES the column
        /// vocabulary, so a reader cannot tell which table an <c>index</c> sort re-ordered
        /// without it. <c>changed</c> separates "the table was already sorted that way" from
        /// "this op re-ordered it", which a bare OK cannot - the <c>op=expand</c>
        /// rule.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildSortPayload(
            string window, string tabToken, string column, bool ascending, bool changed)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.SortOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("tab", TabField(tabToken)),
                new KeyValuePair<string, string>("column", column ?? string.Empty),
                new KeyValuePair<string, string>("dir", DirToken(ascending)),
                new KeyValuePair<string, string>("changed", changed ? "true" : "false"),
            };

        // ================================================================ op=select

        // ----- arg keys -----

        /// <summary><c>op=select</c>'s REQUIRED direction. Closed: <c>true</c> |
        /// <c>false</c>, the one <c>state=</c> vocabulary this seam uses everywhere. Spelled
        /// <c>include</c> rather than <c>state</c> because the affordance it drives is
        /// labelled an INCLUDE on screen and its production field is an EXCLUDED set - a
        /// <c>state=</c> here would leave a reader guessing which of the two polarities the
        /// wire speaks. It always speaks "included".</summary>
        internal const string IncludeArg = "include";

        // ----- key vocabulary -----
        //
        // The same <prefix>:<value> grammar as op=expand, split at the FIRST colon (the value
        // half may carry more), and the same two bulk tokens - reused rather than re-spelled,
        // because a second key grammar in one verb is what the namespaced one exists to
        // prevent.

        /// <summary>A per-vessel row of the Missions tab. The value half resolves in TWO
        /// rungs against the live mission's flattened rows: the row's own
        /// <c>OwnerHeadId</c>, then any ONE of that row's interval keys - so a lane may name
        /// either the vessel or a leg of it, and an interval key with an inner <c>@dock</c> /
        /// <c>/seg</c> suffix survives the split.</summary>
        internal const string VesselKeyPrefix = TestCommandUiState.VesselKeyPrefix;

        /// <summary>A derived cross-tree partner journey
        /// (<c>ForeignDockLink.LinkId</c>).</summary>
        internal const string LinkKeyPrefix = "link";

        /// <summary>
        /// ONE composition interval of a vessel (a <c>MissionCompositionNode.HeadLegId</c>:
        /// the vessel's head recording id for its first interval, then <c>/segN</c> and
        /// <c>@dockM</c> suffixes). Drives the INTERVAL checkbox of the expanded vessel
        /// detail - <c>MissionsWindowUI.ApplyIntervalInclusion</c>, the checkbox's own body -
        /// rather than the per-vessel checkbox <c>vessel:</c> drives.
        ///
        /// <para>The difference is the whole point of the prefix: <c>vessel:</c> resolves a
        /// ROW and writes ALL of that row's own interval keys, so it can only produce an
        /// <c>All</c> or <c>None</c> vessel. <c>leg:</c> writes exactly the one key named,
        /// so unticking a vessel's launch interval START-TRIMS the loop to the
        /// post-separation survivor and leaves the row <c>Partial</c> - the leg-trim the
        /// Missions tab exists to author, and the state <c>vessel:</c> cannot reach.</para>
        ///
        /// <para>Resolved against the SAME flattened rows <c>vessel:</c> reads (every row's
        /// own <c>Intervals</c>, children included); a key no row carries is a
        /// <see cref="SelectKeyUnknownReason"/> listing the live interval keys.</para>
        /// </summary>
        internal const string LegKeyPrefix = "leg";

        private static readonly string[] SelectPrefixes = new[]
        {
            VesselKeyPrefix, LinkKeyPrefix, LegKeyPrefix,
        };

        /// <summary>The select key prefixes, comma-joined for the reject message.</summary>
        internal static string SelectPrefixNames => string.Join(",", SelectPrefixes);

        /// <summary>The windows this op drives, comma-joined for the reject message. One:
        /// the include affordances are the Missions tab's.</summary>
        internal static string SelectableWindowNames => TestCommandUiAction.MissionsWindow;

        /// <summary>Whether <c>op=select</c> is defined for this window.</summary>
        internal static bool WindowHasSelectAffordance(string window)
            => window == TestCommandUiAction.MissionsWindow;

        // ----- select: reject reasons -----

        /// <summary><c>op=select</c> against a window with no include affordance.</summary>
        internal const string SelectUnsupportedWindowReason = "select-unsupported-window";

        /// <summary>No <c>key=</c>.</summary>
        internal const string SelectKeyArgMissingReason = "select-key-arg-missing";

        /// <summary>A <c>key=</c> with no colon, or with a prefix this op does not keep. The
        /// message carries the prefix list.</summary>
        internal const string SelectKeyInvalidReason = "select-key-invalid";

        /// <summary>A well-formed key naming something the live mission does not have (a
        /// vessel head from another save, a link id that no longer derives). The message says
        /// how many of that kind DO exist and lists the first few.</summary>
        internal const string SelectKeyUnknownReason = "select-key-unknown";

        /// <summary>No <c>include=</c> on a single key. REQUIRED - see the class
        /// header.</summary>
        internal const string SelectIncludeArgMissingReason = "select-include-arg-missing";

        /// <summary>An <c>include=</c> outside {true,false}.</summary>
        internal const string SelectIncludeArgInvalidReason = "select-include-arg-invalid";

        /// <summary>
        /// <c>include=</c> together with <c>key=all</c> or <c>key=none</c>.
        ///
        /// <para>Refused rather than resolved by precedence, <c>expand-state-with-bulk-key</c>
        /// verbatim: the bulk tokens CARRY their direction (<c>all</c> includes everything,
        /// <c>none</c> excludes everything), so an <c>include=</c> beside one either agrees
        /// redundantly or contradicts it - and letting the token win would make a step that
        /// reads "exclude everything" include everything.</para>
        /// </summary>
        internal const string SelectIncludeWithBulkKeyReason = "select-include-with-bulk-key";

        /// <summary>There is no mission to drive: no Mission in the store has a COMMITTED
        /// tree. REJECTED rather than a cheerful <c>changed=0</c>: the affordance is
        /// per-mission, so with no mission the op wrote nothing and a capture beside it shows
        /// an empty window.</summary>
        internal const string SelectNoMissionReason = "select-no-mission";

        /// <summary>POST-SETTLE terminal: the write went through and, after a drawn frame,
        /// the named key's classified state disagrees with the request. ERROR - we acted and
        /// the game did not follow. Asserted for a SINGLE key only: a bulk step's honest
        /// report is its counts.</summary>
        internal const string SelectNotAppliedReason = "select-not-applied";

        // ----- select: parses -----

        /// <summary>
        /// Parses <c>op=select</c>'s <c>key=</c>. Yields either a bulk scope or a
        /// prefix/value pair; the value half is everything after the FIRST colon, so a
        /// <c>vessel:rec17@dock2</c> or a <c>link:a:b</c> key keeps its own inner colons.
        /// </summary>
        /// <param name="includeGiven">Whether the step carried an explicit
        /// <c>include=</c>. A bulk key with one is
        /// <see cref="SelectIncludeWithBulkKeyReason"/> rather than a silent
        /// override.</param>
        internal static bool TryParseSelectKey(string window, string raw, bool includeGiven,
                                               out UiSelectScope scope, out string prefix,
                                               out string value, out string rejectReason)
        {
            scope = UiSelectScope.Single;
            prefix = null;
            value = null;

            if (!WindowHasSelectAffordance(window))
            {
                rejectReason = SelectUnsupportedWindowReason;
                return false;
            }
            if (string.IsNullOrEmpty(raw))
            {
                rejectReason = SelectKeyArgMissingReason;
                return false;
            }
            if (raw == TestCommandUiState.ExpandAllToken
                || raw == TestCommandUiState.ExpandNoneToken)
            {
                if (includeGiven)
                {
                    rejectReason = SelectIncludeWithBulkKeyReason;
                    return false;
                }
                scope = raw == TestCommandUiState.ExpandAllToken
                    ? UiSelectScope.All
                    : UiSelectScope.None;
                rejectReason = null;
                return true;
            }

            int colon = raw.IndexOf(':');
            if (colon <= 0 || colon == raw.Length - 1)
            {
                rejectReason = SelectKeyInvalidReason;
                return false;
            }
            string head = raw.Substring(0, colon);
            for (int i = 0; i < SelectPrefixes.Length; i++)
            {
                if (SelectPrefixes[i] != head) continue;
                prefix = head;
                value = raw.Substring(colon + 1);
                rejectReason = null;
                return true;
            }
            rejectReason = SelectKeyInvalidReason;
            return false;
        }

        /// <summary>Parses the <c>include=</c> of a SINGLE key. Required; the bulk tokens
        /// carry their own direction and never reach here.</summary>
        internal static bool TryParseInclude(string raw, out bool include,
                                             out string rejectReason)
        {
            include = false;
            // REQUIRED, so absence is its own reject rather than a default: a single key is
            // driven both ways and a defaulted direction would silently run the opposite
            // half of a lane's check.
            if (raw == null)
            {
                rejectReason = SelectIncludeArgMissingReason;
                return false;
            }
            // The seam's ONE boolean parse for the value half.
            return TestCommandUiState.TryParseBoolArg(
                raw, whenAbsent: false, SelectIncludeArgInvalidReason, out include,
                out rejectReason);
        }

        /// <summary>The direction a bulk scope carries: <c>all</c> includes,
        /// <c>none</c> excludes.</summary>
        internal static bool BulkScopeIncludes(UiSelectScope scope)
            => scope == UiSelectScope.All;

        /// <summary>The wire spelling of an include flag, shared by the payload and the log
        /// line.</summary>
        internal static string IncludeToken(bool include)
            => include ? TestCommandUiState.StateTrueToken
                       : TestCommandUiState.StateFalseToken;

        // ----- select: payload -----

        /// <summary>
        /// OK payload for <c>select</c>:
        /// <c>op=select window= mission= key= include= changed= excluded= links=</c>.
        ///
        /// <para><c>mission</c> is REPORTED rather than taken: a Mission id is save-specific,
        /// so a committed spec cannot name one and the op drives the first mission with a
        /// committed tree (the <c>recording=first</c> rationale). Echoing the resolved id is
        /// what lets a reader tie the edit to a row.</para>
        ///
        /// <para><c>excluded</c> / <c>links</c> are the mission's standing counts AFTER the
        /// write, so one step reports both its own effect and the state it left behind - the
        /// <c>op=expand</c> rule, and here also the only in-answer record of a save edit
        /// (see the class header).</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildSelectPayload(
            string window, string missionId, string key, bool include, int changed,
            int excluded, int links)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.SelectOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("mission", missionId ?? string.Empty),
                new KeyValuePair<string, string>("key", key ?? string.Empty),
                new KeyValuePair<string, string>("include", IncludeToken(include)),
                new KeyValuePair<string, string>("changed", changed.ToString(ic)),
                new KeyValuePair<string, string>("excluded", excluded.ToString(ic)),
                new KeyValuePair<string, string>("links", links.ToString(ic)),
            };
        }
    }
}
