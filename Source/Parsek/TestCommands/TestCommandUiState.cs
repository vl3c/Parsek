using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.InGameTests;

namespace Parsek.TestCommands
{
    /// <summary>Whether an <c>op=expand</c> call names one key or the whole window.</summary>
    internal enum UiExpandScope
    {
        /// <summary>One <c>&lt;prefix&gt;:&lt;value&gt;</c> key.</summary>
        Single = 0,

        /// <summary><c>key=all</c>: every key the window can enumerate.</summary>
        All = 1,

        /// <summary><c>key=none</c>: collapse every key the window can enumerate.</summary>
        None = 2,
    }

    /// <summary>Which production opener an <c>op=target</c> call resolves to.</summary>
    internal enum UiTargetKind
    {
        Mission = 0,
        Route = 1,
    }

    /// <summary>Which popup an <c>op=picker</c> call opens, and therefore which production
    /// opener it calls.</summary>
    internal enum UiPickerMode
    {
        /// <summary><c>GroupPickerUI.OpenForGroup</c>, titled "Set Parent Group".</summary>
        SetParent = 0,

        /// <summary><c>GroupPickerUI.OpenForRecording</c>, titled "Manage Groups".</summary>
        Manage = 1,

        /// <summary><c>LogisticsWindowUI</c>'s round-trip link picker.</summary>
        Link = 2,
    }

    /// <summary>
    /// Pure decision / payload half of the three STATE ops - <c>expand</c>, <c>target</c>
    /// and <c>picker</c> - that reach surfaces a window only draws after a click.
    ///
    /// <para><b>WHY THESE THREE, and why they poke state rather than click.</b> The GUI
    /// census photographed 22 of 105 surfaces. One whole class of the remainder is not
    /// missing data and not outside the recorder: it is drawn only when some set of
    /// expanded keys contains the right string, or when a window was opened THROUGH its
    /// own opener instead of by raising a bare <c>IsOpen</c>. Every one of those is a plain
    /// field or a plain method whose only other caller is a <c>GUILayout.Button</c>
    /// handler, so the seam writes the same field and calls the same method - the
    /// <c>UiAction</c> file header's argument, unchanged. No new player-facing surface, and
    /// no synthesised input.</para>
    ///
    /// <para><b>THE KEY GRAMMAR IS NAMESPACED because the sets are.</b> A window keeps
    /// several independent expansion collections with overlapping key shapes - the Missions
    /// window alone has <c>expandedGroups</c> (group NAMES), <c>expandedChains</c> (block
    /// ids), <c>expandedVessels</c> and <c>collapsedLegs</c> (both
    /// <c>missionId:headId</c>), and <c>digestExpanded</c> (mission ids). A flat key could
    /// not say which one it meant, and two of them share a shape exactly. So the wire key
    /// is <c>&lt;prefix&gt;:&lt;value&gt;</c>, split at the FIRST colon (the value may
    /// carry more).</para>
    ///
    /// <para><b><c>collapsedLegs</c> IS INVERTED and the op hides that.</b> The production
    /// set holds what is COLLAPSED, so <c>state=true</c> on a <c>leg:</c> key REMOVES the
    /// key. Expressing it any other way would put a polarity flip in every spec that used
    /// it; the applier owns the flip and the payload always speaks in "expanded".</para>
    ///
    /// <para><b><c>key=all</c> is the affordance that actually buys the pictures.</b> A
    /// census wants "every group folder open", not one; and the ids it would otherwise need
    /// (mission ids, head ids, route ids) are save-specific, so a committed spec cannot
    /// carry them. The window classes enumerate their own keys, the op applies the state to
    /// all of them, and the payload reports how many changed.</para>
    /// </summary>
    /// <summary>The four counts <c>op=run</c> reports for the category it ran
    /// (<see cref="TestCommandUiState.TallyCategory"/>).</summary>
    internal struct UiRunTally
    {
        internal int Total;
        internal int Passed;
        internal int Failed;
        internal int Skipped;
    }

    internal static class TestCommandUiState
    {
        // ----- arg keys -----

        internal const string KeyArg = "key";
        internal const string StateArg = "state";
        internal const string MissionArg = "mission";
        internal const string RouteArg = "route";
        internal const string GroupArg = "group";

        /// <summary><c>op=run</c>'s REQUIRED in-game test category. Spelled <c>category</c>,
        /// the same key the <c>RunTests</c> verb takes, because it names the same thing -
        /// an <c>[InGameTest(Category = ...)]</c> value - and a second spelling for one
        /// concept is how a spec author picks the wrong one. OPEN valued: the categories
        /// come from the assembly's attributes, so no closed table can carry them (but
        /// they are NOT save-specific either, which is why a COMMITTED spec may name
        /// one).</summary>
        internal const string CategoryArg = "category";
        internal const string RecordingArg = "recording";

        /// <summary>
        /// <c>op=run</c>'s OPTIONAL <c>await=true|false</c>. ABSENT MEANS TRUE, so every
        /// step written before this arg existed keeps its byte-identical two-phase
        /// behaviour (dispatch, hold the FIFO head, report the category's tally once the
        /// batch stops).
        ///
        /// <para><c>await=false</c> terminates the op OK as soon as the batch is confirmed
        /// dispatched and LEAVES IT RUNNING, which is the only way a census can photograph
        /// the runner table in its RUNNING state: under <c>await=true</c> the op cannot
        /// terminate until the batch has stopped, so every capture that follows it is a
        /// picture of results. It additionally unblocks a category longer than
        /// <c>op=run</c>'s 60 s default deferral budget, which under <c>await=true</c>
        /// terminates ERROR <see cref="RunNotFinishedReason"/> however healthy the
        /// batch.</para>
        /// </summary>
        internal const string AwaitArg = "await";

        internal const string StateTrueToken = "true";
        internal const string StateFalseToken = "false";

        /// <summary>
        /// The ONE parse of the seam's one boolean vocabulary, behind every op that reads a
        /// true/false arg: <c>state=</c> (expand, playback, state), <c>await=</c> (run),
        /// <c>dir=</c> is NOT one of these (it is asc/desc), <c>include=</c> (select) and
        /// <c>commit=</c> (edit).
        ///
        /// <para>Four near-identical copies of these five lines existed before this, each
        /// with its own default and its own reject token, and the copies are exactly how a
        /// case-insensitive or a <c>"1"</c>-accepting one gets in. The DEFAULT and the
        /// REJECT TOKEN stay per-op, because those genuinely differ - an absent
        /// <c>commit=</c> means false and an absent <c>await=</c> means true, and each op
        /// names its own refusal - so they are the two parameters rather than the copied
        /// body.</para>
        /// </summary>
        /// <param name="whenAbsent">What an absent arg means for THIS op.</param>
        /// <param name="invalidReason">The op's own reject token for a value outside the
        /// vocabulary.</param>
        internal static bool TryParseBoolArg(string raw, bool whenAbsent,
                                             string invalidReason, out bool value,
                                             out string rejectReason)
        {
            value = whenAbsent;
            rejectReason = null;
            if (raw == null) return true;
            if (raw == StateTrueToken) { value = true; return true; }
            if (raw == StateFalseToken) { value = false; return true; }
            rejectReason = invalidReason;
            return false;
        }

        /// <summary>The <c>recording=</c> convenience value: the first committed recording.
        /// A census cannot name a recording id that is save-specific, and the Manage Groups
        /// popup looks the same over any row, so "the first one" is the honest
        /// selector.</summary>
        internal const string RecordingFirstToken = "first";

        // ----- expand: key vocabulary -----

        internal const string ExpandAllToken = "all";
        internal const string ExpandNoneToken = "none";

        internal const string GroupKeyPrefix = "group";
        internal const string ChainKeyPrefix = "chain";
        internal const string VesselKeyPrefix = "vessel";
        internal const string LegKeyPrefix = "leg";
        internal const string DigestKeyPrefix = "digest";
        internal const string RowKeyPrefix = "row";
        internal const string RosterKeyPrefix = "roster";
        internal const string FlightsKeyPrefix = "flights";

        /// <summary>The Career window's two <c>Pending in timeline</c> folds. ONE prefix
        /// because the window keeps ONE fold collection; the wire VALUES are the tab the
        /// fold belongs to (<c>contracts</c> / <c>strategies</c>), which is what a spec
        /// author already knows, rather than the dotted production key
        /// (<c>Contracts.Pending</c>) that the collection is keyed by.</summary>
        internal const string PendingKeyPrefix = "pending";

        /// <summary>Both test-runner windows' category folds. ONE prefix shared by the two
        /// windows because it names the same collection shape in both - each window keeps
        /// its OWN set, and the window= arg is what says which.</summary>
        internal const string CategoryKeyPrefix = "category";

        private static readonly string[] MissionsExpandPrefixes = new[]
        {
            GroupKeyPrefix, ChainKeyPrefix, VesselKeyPrefix, LegKeyPrefix, DigestKeyPrefix,
        };

        private static readonly string[] LogisticsExpandPrefixes = new[] { RowKeyPrefix };

        // The Career window's fold set is INVERTED like the Missions window's collapsedLegs
        // (membership means FOLDED), and the op always speaks "expanded", so the applier
        // owns the flip. Its three keys are the only foldable state in that window - the
        // Facilities tab has none - and all three live under the split layout, which only
        // appears on a career whose recorded timeline adds rows after now.
        private static readonly string[] CareerExpandPrefixes = new[] { PendingKeyPrefix };

        /// <summary>The three wire values <c>pending:</c> takes: the TAB whose pending fold to
        /// drive. Kept beside the prefix rather than derived from the window's own dotted
        /// keys because the mapping is the point - see
        /// <see cref="CareerFoldKeyFor"/>.</summary>
        internal static readonly string[] CareerPendingFoldValues = new[]
        {
            "contracts", "strategies", "milestones",
        };

        /// <summary>
        /// The window's own fold key for a <c>pending:</c> wire value, or null for an
        /// unknown one (which the enumeration-membership check rejects first).
        ///
        /// <para>Two names for one fold, and the mapping is deliberate: the wire says the
        /// TAB, which is what a spec author and a capture label already name, and the
        /// production collection is keyed by a dotted string that is an implementation
        /// detail of that window. The constants are referenced rather than copied, so a
        /// rename on the window side is a compile error here.</para>
        /// </summary>
        internal static string CareerFoldKeyFor(string value)
        {
            if (value == "contracts")
                return CareerStateWindowUI.GroupKey_ContractsPending;
            if (value == "strategies")
                return CareerStateWindowUI.GroupKey_StrategiesPending;
            if (value == "milestones")
                return CareerStateWindowUI.GroupKey_MilestonesPending;
            return null;
        }

        // The Kerbals window keeps one expansion set per TAB, so it takes one prefix per
        // tab rather than one for the whole window: `roster:` drives a Roster row's
        // replacement-chain view plus that tab's plain-kerbal fold row (keyed
        // KerbalsWindowUI.PlainBucketKey), `flights:` drives a Flights group's fold.
        private static readonly string[] KerbalsExpandPrefixes = new[]
        {
            RosterKeyPrefix, FlightsKeyPrefix,
        };

        // Both runner windows draw ONE fold per in-game test category and nothing else
        // foldable, so they take one prefix. Shared array, two windows: the sets live on
        // the two window objects and `window=` selects between them.
        private static readonly string[] TestRunnerExpandPrefixes = new[]
        {
            CategoryKeyPrefix,
        };

        /// <summary>The windows whose expansion state this op drives, comma-joined for the
        /// reject message.</summary>
        internal static string ExpandableWindowNames =>
            TestCommandUiAction.MissionsWindow + "," + TestCommandUiAction.LogisticsWindow
            + "," + TestCommandUiAction.KerbalsWindow
            + "," + TestCommandUiAction.CareerWindow
            + "," + TestCommandUiAction.TestRunnerWindow
            + "," + TestCommandUiAction.TestRunnerGlobalWindow;

        /// <summary>The key prefixes a window accepts, or null when the window keeps no
        /// expansion state this op can drive.</summary>
        internal static string[] ExpandPrefixesFor(string window)
        {
            if (window == TestCommandUiAction.MissionsWindow) return MissionsExpandPrefixes;
            if (window == TestCommandUiAction.LogisticsWindow) return LogisticsExpandPrefixes;
            if (window == TestCommandUiAction.KerbalsWindow) return KerbalsExpandPrefixes;
            if (window == TestCommandUiAction.CareerWindow) return CareerExpandPrefixes;
            if (window == TestCommandUiAction.TestRunnerWindow) return TestRunnerExpandPrefixes;
            if (window == TestCommandUiAction.TestRunnerGlobalWindow) return TestRunnerExpandPrefixes;
            return null;
        }

        /// <summary>Comma-joined prefixes for a window, for the reject message.</summary>
        internal static string ExpandPrefixNamesFor(string window)
        {
            string[] prefixes = ExpandPrefixesFor(window);
            return prefixes == null ? string.Empty : string.Join(",", prefixes);
        }

        // ----- reject reasons -----

        /// <summary><c>op=expand</c> against a window with no driveable expansion state.
        /// The message names the ones that have it.</summary>
        internal const string ExpandUnsupportedWindowReason = "expand-unsupported-window";

        /// <summary>No <c>key=</c>.</summary>
        internal const string ExpandKeyArgMissingReason = "expand-key-arg-missing";

        /// <summary>A <c>key=</c> with no colon, or with a prefix the named window does not
        /// keep. The message carries that window's own prefix list.</summary>
        internal const string ExpandKeyInvalidReason = "expand-key-invalid";

        /// <summary>A well-formed key naming something the live window does not have (a
        /// group that no longer exists, a mission id from another save). The message names
        /// how many keys of that prefix DO exist and lists the first few.</summary>
        internal const string ExpandKeyUnknownReason = "expand-key-unknown";

        /// <summary>A <c>state=</c> outside {true,false}.</summary>
        internal const string StateArgInvalidReason = "state-arg-invalid";

        /// <summary>
        /// <c>state=</c> together with <c>key=all</c> or <c>key=none</c>.
        ///
        /// <para>Refused rather than resolved by precedence, the <c>op=pointer</c>
        /// park-versus-coordinates rule: the bulk tokens CARRY their direction
        /// (<c>all</c> expands, <c>none</c> collapses), so a <c>state=</c> beside one
        /// either agrees redundantly or contradicts it - and silently letting the token
        /// win would make a step that reads "collapse everything" expand
        /// everything.</para>
        /// </summary>
        internal const string ExpandStateWithBulkKeyReason = "expand-state-with-bulk-key";

        /// <summary><c>op=target</c> against a window with no targeted opener. Only
        /// <c>structure</c> has one today.</summary>
        internal const string TargetUnsupportedWindowReason = "target-unsupported-window";

        /// <summary>Neither <c>mission=</c> nor <c>route=</c>.</summary>
        internal const string TargetArgMissingReason = "target-arg-missing";

        /// <summary>BOTH <c>mission=</c> and <c>route=</c>. Refused rather than resolved by
        /// precedence: the two open different lists, and guessing would photograph the
        /// wrong one under the caller's label.</summary>
        internal const string TargetArgConflictReason = "target-arg-conflict";

        /// <summary>The named mission / route does not exist. The message lists what
        /// does.</summary>
        internal const string TargetNotFoundReason = "target-not-found";

        // ----- run: reject reasons -----
        //
        // `op=run` drives ONE WINDOW'S OWN InGameTestRunner, which is the only runner whose
        // results either runner window draws. Every reason below is fail-closed: the
        // alternative to each is a capture labelled "results" over a table that shows none.

        /// <summary><c>op=run</c> against a window that owns no test runner. The message
        /// names the two that do.</summary>
        internal const string RunUnsupportedWindowReason = "run-unsupported-window";

        /// <summary>No <c>category=</c>. REQUIRED, never defaulted to "everything": the
        /// full batch is minutes of tests and half of them mutate the save, which is not
        /// something a census step should be able to ask for by omission.</summary>
        internal const string RunCategoryArgMissingReason = "run-category-arg-missing";

        /// <summary>A <c>category=</c> matching ZERO tests in that window's own discovery.
        /// REJECTED rather than run: an empty batch reports <c>total=0</c>, leaves the
        /// table untouched, and photographs exactly like a batch that never started.</summary>
        internal const string RunCategoryUnknownReason = "run-category-unknown";

        /// <summary>The window's runner does not exist yet, because the window has not
        /// drawn once (both runners are created lazily on first draw). The remedy is an
        /// <c>op=open</c> step - which settles on a DRAWN frame - before the run.</summary>
        internal const string RunRunnerNotReadyReason = "run-runner-not-ready";

        /// <summary>That runner is already running a batch (or the addon's own is). Refused
        /// rather than queued: <c>RunCategory</c> silently returns while
        /// <c>IsRunning</c>, so a queued-looking step would report a batch it never
        /// started.</summary>
        internal const string RunAlreadyRunningReason = "run-already-running";

        /// <summary>POST-CALL terminal: the budget ran out with the batch still running.
        /// Not a refusal - the batch was dispatched and is simply slower than the verb's
        /// bound. UNREACHABLE under <c>await=false</c>, which never polls.</summary>
        internal const string RunNotFinishedReason = "run-not-finished";

        /// <summary>An <c>await=</c> outside {true,false}. REJECTED rather than treated as
        /// absent: the two readings differ by whether the op waits minutes or returns this
        /// frame, so a typo must not silently pick one.</summary>
        internal const string RunAwaitArgInvalidReason = "run-await-arg-invalid";

        /// <summary><c>op=picker</c> against a window with no row-armed popup.</summary>
        internal const string PickerUnsupportedWindowReason = "picker-unsupported-window";

        /// <summary>No selector arg for the named window.</summary>
        internal const string PickerArgMissingReason = "picker-arg-missing";

        /// <summary>More than one selector arg.</summary>
        internal const string PickerArgConflictReason = "picker-arg-conflict";

        /// <summary>The named group / recording / route does not exist.</summary>
        internal const string PickerTargetNotFoundReason = "picker-target-not-found";

        /// <summary>POST-SETTLE: the opener ran and the popup's own open flag is down. The
        /// one live cause is a popup that closes itself on its first draw; reported rather
        /// than accepted, so a census cannot photograph a scene without the popup under a
        /// label that claims one.</summary>
        internal const string PickerNotOpenedReason = "picker-not-opened";

        /// <summary>POST-SETTLE for <c>op=target</c>: the window's open flag is down after
        /// the opener ran.</summary>
        internal const string TargetNotOpenedReason = "target-not-opened";

        // ----- parses -----

        /// <summary>Parses the optional <c>state=</c>. Absent means <c>true</c>: expanding
        /// is what a census asks for, and a lane that wants the other direction says
        /// so.</summary>
        internal static bool TryParseState(string raw, out bool expanded, out string rejectReason)
        {
            // ABSENT MEANS TRUE: expanding is what a census asks for, and a lane that
            // wants the other direction says so.
            return TryParseBoolArg(raw, whenAbsent: true, StateArgInvalidReason,
                                   out expanded, out rejectReason);
        }

        /// <summary>
        /// Parses <c>key=</c> for a window. Yields either a bulk scope or a
        /// prefix/value pair; the value half is everything after the FIRST colon, so a
        /// <c>vessel:m17:head4</c> key keeps its own inner colon.
        /// </summary>
        /// <param name="stateGiven">Whether the step carried an explicit
        /// <c>state=</c>. A bulk key with one is <see cref="ExpandStateWithBulkKeyReason"/>
        /// rather than a silent override.</param>
        internal static bool TryParseExpandKey(string window, string raw, bool stateGiven,
                                               out UiExpandScope scope, out string prefix,
                                               out string value, out string rejectReason)
        {
            scope = UiExpandScope.Single;
            prefix = null;
            value = null;

            string[] prefixes = ExpandPrefixesFor(window);
            if (prefixes == null)
            {
                rejectReason = ExpandUnsupportedWindowReason;
                return false;
            }
            if (string.IsNullOrEmpty(raw))
            {
                rejectReason = ExpandKeyArgMissingReason;
                return false;
            }
            if (raw == ExpandAllToken || raw == ExpandNoneToken)
            {
                if (stateGiven)
                {
                    rejectReason = ExpandStateWithBulkKeyReason;
                    return false;
                }
                scope = raw == ExpandAllToken ? UiExpandScope.All : UiExpandScope.None;
                rejectReason = null;
                return true;
            }

            int colon = raw.IndexOf(':');
            if (colon <= 0 || colon == raw.Length - 1)
            {
                rejectReason = ExpandKeyInvalidReason;
                return false;
            }
            string head = raw.Substring(0, colon);
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (prefixes[i] != head) continue;
                prefix = head;
                value = raw.Substring(colon + 1);
                rejectReason = null;
                return true;
            }
            rejectReason = ExpandKeyInvalidReason;
            return false;
        }

        /// <summary>Parses <c>op=target</c>'s selector. Exactly one of the two.</summary>
        internal static bool TryParseTarget(string window, string rawMission, string rawRoute,
                                            out UiTargetKind kind, out string value,
                                            out string rejectReason)
        {
            kind = UiTargetKind.Mission;
            value = null;
            if (window != TestCommandUiAction.StructureWindow)
            {
                rejectReason = TargetUnsupportedWindowReason;
                return false;
            }
            bool hasMission = !string.IsNullOrEmpty(rawMission);
            bool hasRoute = !string.IsNullOrEmpty(rawRoute);
            if (hasMission && hasRoute)
            {
                rejectReason = TargetArgConflictReason;
                return false;
            }
            if (!hasMission && !hasRoute)
            {
                rejectReason = TargetArgMissingReason;
                return false;
            }
            kind = hasMission ? UiTargetKind.Mission : UiTargetKind.Route;
            value = hasMission ? rawMission : rawRoute;
            rejectReason = null;
            return true;
        }

        /// <summary>The wire token for a target kind.</summary>
        internal static string TargetKindToken(UiTargetKind kind)
            => kind == UiTargetKind.Mission ? "mission" : "route";

        /// <summary>
        /// Parses <c>op=picker</c>'s selector, which is per-window: the Missions window
        /// takes <c>group=</c> (Set Parent Group) or <c>recording=</c> (Manage Groups), and
        /// the Logistics window takes <c>route=</c> (the round-trip link picker).
        /// </summary>
        internal static bool TryParsePicker(string window, string rawGroup,
                                            string rawRecording, string rawRoute,
                                            out UiPickerMode mode, out string value,
                                            out string rejectReason)
        {
            mode = UiPickerMode.SetParent;
            value = null;
            bool hasGroup = !string.IsNullOrEmpty(rawGroup);
            bool hasRecording = !string.IsNullOrEmpty(rawRecording);
            bool hasRoute = !string.IsNullOrEmpty(rawRoute);

            if (window == TestCommandUiAction.MissionsWindow)
            {
                if (hasRoute || (hasGroup && hasRecording))
                {
                    rejectReason = PickerArgConflictReason;
                    return false;
                }
                if (!hasGroup && !hasRecording)
                {
                    rejectReason = PickerArgMissingReason;
                    return false;
                }
                mode = hasGroup ? UiPickerMode.SetParent : UiPickerMode.Manage;
                value = hasGroup ? rawGroup : rawRecording;
                rejectReason = null;
                return true;
            }
            if (window == TestCommandUiAction.LogisticsWindow)
            {
                if (hasGroup || hasRecording)
                {
                    rejectReason = PickerArgConflictReason;
                    return false;
                }
                if (!hasRoute)
                {
                    rejectReason = PickerArgMissingReason;
                    return false;
                }
                mode = UiPickerMode.Link;
                value = rawRoute;
                rejectReason = null;
                return true;
            }
            rejectReason = PickerUnsupportedWindowReason;
            return false;
        }

        /// <summary>The windows with a row-armed popup, comma-joined for the reject
        /// message.</summary>
        internal static string PickerWindowNames =>
            TestCommandUiAction.MissionsWindow + "," + TestCommandUiAction.LogisticsWindow;

        /// <summary>The wire token for a picker mode.</summary>
        internal static string PickerModeToken(UiPickerMode mode)
        {
            switch (mode)
            {
                case UiPickerMode.SetParent: return "setparent";
                case UiPickerMode.Manage: return "manage";
                default: return "link";
            }
        }

        // ----- candidate lists for the reject messages -----

        /// <summary>
        /// Formats up to <paramref name="cap"/> of the live keys / names for a
        /// "what does exist" reject message, with a <c>(+N more)</c> tail.
        ///
        /// <para>Capped because the list is UNBOUNDED in the save - a dense career carries
        /// hundreds of group names - and the response line is one line. The cap is what
        /// keeps a reject readable instead of truncated by the reader.</para>
        /// </summary>
        internal static string FormatCandidates(IList<string> names, int cap)
        {
            if (names == null || names.Count == 0) return "-";
            var shown = new List<string>();
            int n = names.Count < cap ? names.Count : cap;
            for (int i = 0; i < n; i++) shown.Add(names[i] ?? string.Empty);
            string joined = string.Join(",", shown.ToArray());
            int rest = names.Count - n;
            return rest > 0
                ? joined + "(+" + rest.ToString(CultureInfo.InvariantCulture) + " more)"
                : joined;
        }

        /// <summary>How many candidates a reject message lists before it says "+N more".</summary>
        internal const int CandidateListCap = 8;

        // ----- payloads -----

        /// <summary>
        /// OK payload for <c>expand</c>:
        /// <c>op=expand window= key= state= changed= expanded= total=</c>.
        ///
        /// <para><c>changed</c> is what a lane needs and a bare success verdict cannot say:
        /// <c>changed=0</c> on a <c>key=all</c> step means the window was already in that
        /// state, which reads differently from <c>changed=16</c> in a capture that came out
        /// looking the same. <c>expanded</c> / <c>total</c> are the window's whole set after
        /// the write, so one step reports both its own effect and the standing state.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildExpandPayload(
            string window, string key, bool expanded, int changed, int expandedCount,
            int total)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.ExpandOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("key", key ?? string.Empty),
                new KeyValuePair<string, string>("state", expanded ? "true" : "false"),
                new KeyValuePair<string, string>("changed", changed.ToString(ic)),
                new KeyValuePair<string, string>("expanded", expandedCount.ToString(ic)),
                new KeyValuePair<string, string>("total", total.ToString(ic)),
            };
        }

        /// <summary>The two windows whose own runner <c>op=run</c> can drive, comma-joined
        /// for the <see cref="RunUnsupportedWindowReason"/> message.</summary>
        internal static string RunnableWindowNames =>
            TestCommandUiAction.TestRunnerWindow + ","
            + TestCommandUiAction.TestRunnerGlobalWindow;

        /// <summary>Whether <c>op=run</c> is defined for this window.</summary>
        internal static bool WindowOwnsTestRunner(string window)
            => window == TestCommandUiAction.TestRunnerWindow
               || window == TestCommandUiAction.TestRunnerGlobalWindow;

        /// <summary>
        /// Parses <c>op=run</c>'s window + <c>category=</c> pair. A whitespace-only
        /// category is treated as MISSING-and-written, the <c>RunTests</c>
        /// <c>IsEmptyCategoryArg</c> rule: <c>category = ""</c> is a typo, and the only
        /// other reading would be "run everything".
        /// </summary>
        internal static bool TryParseRun(string window, string rawCategory,
                                         out string category, out string rejectReason)
        {
            category = null;
            if (!WindowOwnsTestRunner(window))
            {
                rejectReason = RunUnsupportedWindowReason;
                return false;
            }
            if (rawCategory == null || rawCategory.Trim().Length == 0)
            {
                rejectReason = RunCategoryArgMissingReason;
                return false;
            }
            category = rawCategory;
            rejectReason = null;
            return true;
        }

        /// <summary>
        /// Parses <c>op=run</c>'s optional <see cref="AwaitArg"/>. Absent means TRUE (the
        /// pre-existing behaviour), and the token set is <c>state=</c>'s so one spelling of
        /// a boolean serves the whole op.
        /// </summary>
        internal static bool TryParseAwait(string raw, out bool awaitBatch,
                                          out string rejectReason)
        {
            // ABSENT MEANS TRUE: every lane written before `await=` is byte-identical.
            return TryParseBoolArg(raw, whenAbsent: true, RunAwaitArgInvalidReason,
                                   out awaitBatch, out rejectReason);
        }

        /// <summary>
        /// Whether a verb may execute while an in-game test batch THIS SEAM started under
        /// <c>await=false</c> is running.
        ///
        /// <para><b>DERIVED, never a second list.</b> The answer is
        /// <c>TestCommandVerbs.IsStateMutatingVerb</c> inverted - the existing named
        /// concept for "cannot change anything a save would capture" - minus
        /// <c>FlushAndQuit</c>. A copied literal set would drift from that one silently,
        /// and the consequence of drift here is a command executing in the middle of a
        /// batch whose campaign-isolation baseline it can corrupt.</para>
        ///
        /// <para><b>WHY <c>FlushAndQuit</c> IS EXCLUDED</b> although the shared set lists
        /// it: it is non-mutating only in the sense that it does not change the world, and
        /// it ENDS THE PROCESS. Quitting mid-batch skips the runner's own baseline revert
        /// and leaves the batch marker in <c>persistent.sfs</c> for the next process's
        /// crash reconcile to clean up. Held instead, which costs a lane nothing: the
        /// batch stops, the relaxation clears, and the quit runs on the next frame.</para>
        /// </summary>
        internal static bool IsBatchGateRelaxableVerb(string verb)
        {
            if (string.IsNullOrEmpty(verb)) return false;
            if (verb == FlushAndQuitVerb) return false;
            return !TestCommandVerbs.IsStateMutatingVerb(verb);
        }

        /// <summary>The one verb <see cref="IsBatchGateRelaxableVerb"/> subtracts from the
        /// non-mutating set. Named so the pinning cell can assert the subtraction is
        /// exactly this and nothing else.</summary>
        internal const string FlushAndQuitVerb = "FlushAndQuit";

        /// <summary>
        /// OK payload for <c>run</c> under <c>await=false</c>:
        /// <c>op=run window= category= started= finished= discovered= running=</c>.
        ///
        /// <para><b>A SEPARATE SHAPE, not <see cref="BuildRunPayload"/> with mid-batch
        /// numbers.</b> The tally keys are meaningless while a batch runs, and not merely
        /// incomplete: <see cref="TallyCategory"/> counts every row whose
        /// <c>Status != NotRun</c> into <c>Total</c>, and a row that is currently
        /// <c>TestStatus.Running</c> lands in NONE of the passed / failed / skipped
        /// buckets - so a mid-batch <c>BuildRunPayload</c> would publish a
        /// <c>total</c> that does not equal its three parts, under the same keys a
        /// finished lane gates on. This payload carries no tally at all.</para>
        ///
        /// <para><c>finished=</c> is <c>!running=</c> by construction, and both are present
        /// so a spec can pin either without writing a negation. <c>finished=true</c> is a
        /// LEGITIMATE outcome rather than an error: <c>InGameTestRunner.RunBatch</c> has no
        /// unconditional yield before it clears <c>isRunning</c>, so a category whose every
        /// cell is scene-ineligible completes inside the <c>StartCoroutine</c> call and the
        /// batch is already over when this payload is built.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildRunStartedPayload(
            string window, string category, int discovered, bool running)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.RunOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("category", category ?? string.Empty),
                new KeyValuePair<string, string>("started", "true"),
                new KeyValuePair<string, string>("finished", running ? "false" : "true"),
                new KeyValuePair<string, string>("discovered", discovered.ToString(ic)),
                new KeyValuePair<string, string>("running", running ? "true" : "false"),
            };
        }

        /// <summary>
        /// The four counts for ONE category's rows, over a runner's whole discovery.
        ///
        /// <para><b>WHY NOT <c>runner.Passed</c> / <c>Failed</c> / <c>Skipped</c>.</b>
        /// Those are recomputed by <c>InGameTestRunner.RecountResults</c> over EVERY
        /// discovered test, and <c>ResetCategory</c> clears only the named category, so on
        /// a runner that has run more than one category they carry the other categories'
        /// rows as well. A lane whose second <c>op=run</c> step gated on <c>failed=0</c>
        /// would then be reading the first step's failures - or passing on the first
        /// step's passes - which is the one thing the wire numbers exist to prevent.</para>
        ///
        /// <para><c>Total</c> is the CONSIDERED count (<c>Status != NotRun</c>), the same
        /// quantity the runner's own <c>BATCH_COMPLETE</c> line calls <c>total</c>, so a
        /// category whose cells were all scene-skipped reads <c>total=N skipped=N</c>
        /// rather than <c>total=0</c>.</para>
        /// </summary>
        internal static UiRunTally TallyCategory(
            IReadOnlyList<InGameTestInfo> tests, string category)
        {
            var tally = new UiRunTally();
            if (tests == null || category == null) return tally;
            for (int i = 0; i < tests.Count; i++)
            {
                InGameTestInfo test = tests[i];
                if (test == null) continue;
                // ORDINAL, matching the runner's own category filters and the applier's
                // pre-dispatch discovery count: a culture-aware compare could tally a
                // category the batch never ran.
                if (!string.Equals(test.Category, category, StringComparison.Ordinal))
                    continue;
                switch (test.Status)
                {
                    case TestStatus.Passed: tally.Passed++; break;
                    case TestStatus.Failed: tally.Failed++; break;
                    case TestStatus.Skipped: tally.Skipped++; break;
                    case TestStatus.NotRun: continue;
                }
                tally.Total++;
            }
            return tally;
        }

        /// <summary>
        /// OK payload for <c>run</c>:
        /// <c>op=run window= category= total= passed= failed= skipped=</c>.
        ///
        /// <para>The four numbers are THAT CATEGORY's rows (<see cref="TallyCategory"/>),
        /// not the runner's whole-discovery counters, so a lane that runs two categories
        /// through one window reads each step's own outcome. The window's summary LABEL
        /// draws the whole-runner counters instead, so the two agree only on a runner that
        /// has run one category - which is the normal census shape and is why the log line
        /// carries both the tally and the discovered count.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildRunPayload(
            string window, string category, int total, int passed, int failed, int skipped)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.RunOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("category", category ?? string.Empty),
                new KeyValuePair<string, string>("total", total.ToString(ic)),
                new KeyValuePair<string, string>("passed", passed.ToString(ic)),
                new KeyValuePair<string, string>("failed", failed.ToString(ic)),
                new KeyValuePair<string, string>("skipped", skipped.ToString(ic)),
            };
        }

        /// <summary>OK payload for <c>target</c>:
        /// <c>op=target window= target= id= title= steps= open=</c>. <c>steps</c> is the
        /// row count the opener's own Rebuild produced, which is the difference between an
        /// opened-and-populated window and the empty chrome the census photographed.</summary>
        internal static List<KeyValuePair<string, string>> BuildTargetPayload(
            string window, UiTargetKind kind, string id, string title, int steps, bool open)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.TargetOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("target", TargetKindToken(kind)),
                new KeyValuePair<string, string>("id", id ?? string.Empty),
                new KeyValuePair<string, string>("title", title ?? string.Empty),
                new KeyValuePair<string, string>("steps", steps.ToString(ic)),
                new KeyValuePair<string, string>("open", open ? "true" : "false"),
            };
        }

        /// <summary>OK payload for <c>picker</c>:
        /// <c>op=picker window= picker= target= open=</c>.</summary>
        internal static List<KeyValuePair<string, string>> BuildPickerPayload(
            string window, UiPickerMode mode, string target, bool open)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.PickerOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("picker", PickerModeToken(mode)),
                new KeyValuePair<string, string>("target", target ?? string.Empty),
                new KeyValuePair<string, string>("open", open ? "true" : "false"),
            };
    }
}
