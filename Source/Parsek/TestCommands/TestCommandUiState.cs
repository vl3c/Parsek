using System;
using System.Collections.Generic;
using System.Globalization;

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
    internal static class TestCommandUiState
    {
        // ----- arg keys -----

        internal const string KeyArg = "key";
        internal const string StateArg = "state";
        internal const string MissionArg = "mission";
        internal const string RouteArg = "route";
        internal const string GroupArg = "group";
        internal const string RecordingArg = "recording";

        internal const string StateTrueToken = "true";
        internal const string StateFalseToken = "false";

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

        private static readonly string[] MissionsExpandPrefixes = new[]
        {
            GroupKeyPrefix, ChainKeyPrefix, VesselKeyPrefix, LegKeyPrefix, DigestKeyPrefix,
        };

        private static readonly string[] LogisticsExpandPrefixes = new[] { RowKeyPrefix };

        /// <summary>The windows whose expansion state this op drives, comma-joined for the
        /// reject message.</summary>
        internal static string ExpandableWindowNames =>
            TestCommandUiAction.MissionsWindow + "," + TestCommandUiAction.LogisticsWindow;

        /// <summary>The key prefixes a window accepts, or null when the window keeps no
        /// expansion state this op can drive.</summary>
        internal static string[] ExpandPrefixesFor(string window)
        {
            if (window == TestCommandUiAction.MissionsWindow) return MissionsExpandPrefixes;
            if (window == TestCommandUiAction.LogisticsWindow) return LogisticsExpandPrefixes;
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
            expanded = true;
            rejectReason = null;
            if (raw == null) return true;
            if (raw == StateTrueToken) { expanded = true; return true; }
            if (raw == StateFalseToken) { expanded = false; return true; }
            rejectReason = StateArgInvalidReason;
            return false;
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
