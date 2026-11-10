using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Xunit;
using C = Parsek.Tests.RecordingsTableClickHandlerCharacterizationTests;
using WatchResult = Parsek.GhostPlaybackLogic.GroupWatchAdvanceResult;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization goldens for the decision blocks of the Recordings tab's folder row,
    /// <c>RecordingsTableUI.DrawGroupTree</c>, that hold no GUI call: the Enable and Archive
    /// aggregates, the folder playback write, the expand arrow and header label, the header
    /// click (expand / collapse or double-click rename), the rename Enter / Escape keys, which
    /// pair of buttons the G cell draws, the Loop scope, and the Watch button's
    /// watched-descendant test, rotation, enable state, transition-log gate, tooltip and press
    /// kind. The draw method cannot run headless, so each block is pinned through an ORACLE:
    /// the original block copied verbatim from the source (whole-line comments dropped,
    /// non-ASCII characters in literals written as \u escapes, private table state reached
    /// through reflection on the same table, the one Unity read - the click time - and the one
    /// Unity write - Event.current.Use() - turned into a parameter and a return value, and a
    /// branch that only draws replaced by the number of the branch it took). Each sweep runs
    /// every branch of its block and prints the block's outputs and the state it leaves; a
    /// golden is that print.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableGroupTreeCharacterizationTests : IDisposable
    {
        internal readonly List<string> logLines = new List<string>();

        public RecordingsTableGroupTreeCharacterizationTests()
        {
            C.ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            C.ResetAll();
        }

        // ---------------------------------------------------------------------------------
        // Private table state, reached by name.
        // ---------------------------------------------------------------------------------

        private static FieldInfo Field(string name)
        {
            FieldInfo f = typeof(RecordingsTableUI).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException("RecordingsTableUI", name);
            return f;
        }

        internal static T Get<T>(RecordingsTableUI ui, string name) => (T)Field(name).GetValue(ui);

        internal static void Set(RecordingsTableUI ui, string name, object value) => Field(name).SetValue(ui, value);

        private static readonly MethodInfo CommitRecordingRenameMethod = typeof(RecordingsTableUI)
            .GetMethod("CommitRecordingRename", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void CommitRecordingRename(RecordingsTableUI ui, IReadOnlyList<Recording> committed)
        {
            CommitRecordingRenameMethod.Invoke(ui, new object[] { committed });
        }

        // ---------------------------------------------------------------------------------
        // The oracles: the ORIGINAL blocks of DrawGroupTree, verbatim.
        // ---------------------------------------------------------------------------------

        // Enable checkbox state.
        internal static bool OracleAllEnabled(HashSet<int> descendants, IReadOnlyList<Recording> committed, int memberCount)
        {
            int enabledCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].PlaybackEnabled) enabledCount++;
            bool allEnabled = memberCount > 0 && enabledCount == memberCount;
            return allEnabled;
        }

        // Enable checkbox write.
        internal static void OracleApplyEnabled(string groupName, HashSet<int> descendants,
            IReadOnlyList<Recording> committed, bool newEnabled)
        {
            foreach (int idx in descendants)
                RecordingStore.SetRecordingPlaybackEnabled(committed[idx], newEnabled);
            ParsekLog.Info("UI", $"Set playback enabled for group '{groupName}': enabled={newEnabled}");
        }

        internal static string OracleArrow(bool expanded)
        {
            string arrow = expanded ? "\u25bc" : "\u25b6";
            return arrow;
        }

        // The rename editor's Enter / Escape: `Event.current.Use()` is the returned true.
        internal static bool OracleRenameKeys(RecordingsTableUI ui, string groupName, bool submitGrp, bool cancelGrp)
        {
            bool used = false;
            if (submitGrp)
            {
                ui.CommitGroupRenameForTesting(groupName);
                used = true;
            }
            else if (cancelGrp)
            {
                Set(ui, "renamingGroup", null);
                Set(ui, "activeRenameRect", default(Rect));
                used = true;
            }
            return used;
        }

        internal static string OracleHeaderLabel(string treeConnector, string arrow, string groupLabel, int memberCount)
        {
            return $"{treeConnector ?? ""}{arrow} {groupLabel} ({memberCount})";
        }

        // The header button's click body; `t` is the Time.realtimeSinceStartup read.
        internal static bool OracleHeaderClick(RecordingsTableUI ui, string groupName,
            IReadOnlyList<Recording> committed, bool expanded, int memberCount, float t)
        {
            if (Get<string>(ui, "lastClickedGroup") == groupName
                && t - Get<float>(ui, "lastGroupClickTime") < 0.3f)
            {
                if (Get<string>(ui, "renamingRecordingId") != null)
                    CommitRecordingRename(ui, committed);
                if (Get<string>(ui, "renamingGroup") != null)
                    ui.CommitGroupRenameForTesting(Get<string>(ui, "renamingGroup"));
                Set(ui, "renamingGroup", groupName);
                Set(ui, "renamingGroupText", groupName);
                Set(ui, "renamingGroupFocused", false);
                Set(ui, "lastClickedGroup", null);
            }
            else
            {
                var expandedGroups = Get<HashSet<string>>(ui, "expandedGroups");
                if (expanded) expandedGroups.Remove(groupName);
                else expandedGroups.Add(groupName);
                expanded = !expanded;
                Set(ui, "lastClickedGroup", groupName);
                Set(ui, "lastGroupClickTime", t);
                ParsekLog.Verbose("UI", $"Group '{groupName}' {(expanded ? "expanded" : "collapsed")} ({memberCount} recordings)");
            }
            return expanded;
        }

        // The G cell: 0 = G + S (absorbed launched vessel), 1 = G + X (disbandable), 2 = G alone.
        internal static int OracleManageCell(bool hasAbsorbedRootMembers, bool canDisbandGroup)
        {
            if (hasAbsorbedRootMembers)
            {
                return 0;
            }
            else if (canDisbandGroup)
            {
                return 1;
            }
            else
            {
                return 2;
            }
        }

        internal static IEnumerable<int> OracleLoopScope(HashSet<int> descendants, List<int> absorbedRootMembers)
        {
            IEnumerable<int> grpLoopScope = descendants;
            if (absorbedRootMembers != null)
            {
                var union = new HashSet<int>(descendants);
                union.UnionWith(absorbedRootMembers);
                grpLoopScope = union;
            }
            return grpLoopScope;
        }

        internal static bool OracleAnyWatched(int watchedIdx, HashSet<int> descendants, IReadOnlyList<Recording> committed)
        {
            bool anyDescendantWatched =
                watchedIdx >= 0
                && descendants.Contains(watchedIdx)
                && watchedIdx < committed.Count
                && committed[watchedIdx] != null;
            return anyDescendantWatched;
        }

        internal static WatchResult OracleRotation(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            Func<int, bool> isEligibleForWatch, string cursorRecId, string watchedDescendantRecId,
            out int nextTargetIdxOut)
        {
            GhostPlaybackLogic.GroupWatchAdvanceResult rotation = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, isEligibleForWatch, cursorRecId, watchedDescendantRecId);

            int nextTargetIdx = rotation.NextRecordingId != null
                ? GhostPlaybackLogic.FindRecordingIndexById(committed, rotation.NextRecordingId)
                : -1;

            if (nextTargetIdx < 0)
                rotation = GhostPlaybackLogic.GroupWatchAdvanceResult.Empty;

            nextTargetIdxOut = nextTargetIdx;
            return rotation;
        }

        internal static bool OracleCanWatch(WatchResult rotation, int nextTargetIdx, bool anyDescendantWatched,
            out bool isToggleOffPressOut)
        {
            bool canWatch = rotation.NextRecordingId != null && nextTargetIdx >= 0 && !rotation.IsToggleOff;
            bool isToggleOffPress = rotation.IsToggleOff && anyDescendantWatched;
            isToggleOffPressOut = isToggleOffPress;
            return canWatch;
        }

        // The transition-log gate: true is "the Info line is written".
        internal static bool OracleTransition(Dictionary<string, bool> lastCanWatchByGroup,
            Dictionary<string, string> lastResolvedWatchTargetByGroup, string groupWatchKey,
            bool canWatch, string resolvedTargetId)
        {
            bool prevGroupCanWatch;
            string prevResolvedTargetId;
            if (!lastCanWatchByGroup.TryGetValue(groupWatchKey, out prevGroupCanWatch)
                || prevGroupCanWatch != canWatch
                || !lastResolvedWatchTargetByGroup.TryGetValue(groupWatchKey, out prevResolvedTargetId)
                || prevResolvedTargetId != resolvedTargetId)
            {
                lastCanWatchByGroup[groupWatchKey] = canWatch;
                lastResolvedWatchTargetByGroup[groupWatchKey] = resolvedTargetId;
                return true;
            }
            return false;
        }

        internal static string OracleWatchTooltip(WatchResult rotation, bool isWatching, int nextTargetIdx,
            string watchedDescendantRecId, IReadOnlyList<Recording> committed)
        {
            string watchTooltip;
            if (rotation.TotalEligible == 0)
                watchTooltip = "no watchable vessels in this group";
            else if (isWatching && nextTargetIdx >= 0 && rotation.NextRecordingId != watchedDescendantRecId)
                watchTooltip = $"switch to {committed[nextTargetIdx].VesselName}";
            else if (isWatching && rotation.IsToggleOff)
                watchTooltip = "exit watch (no other watchable vessels)";
            else if (nextTargetIdx >= 0)
                watchTooltip = $"enter watch on {committed[nextTargetIdx].VesselName}";
            else
                watchTooltip = RecordingsTableUI.GetWatchButtonTooltip(isWatching, false, false, false, false);
            return watchTooltip;
        }

        internal static string OracleAdvancePressKind(string cursorRecId, bool isWrap)
        {
            string pressKind = string.IsNullOrEmpty(cursorRecId)
                ? "enter"
                : (isWrap ? "wrap" : "advance");
            return pressKind;
        }

        // Archive checkbox state.
        internal static bool OracleAllHidden(HashSet<int> descendants, IReadOnlyList<Recording> committed, int memberCount)
        {
            int hiddenCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].Hidden) hiddenCount++;
            bool allHidden = memberCount > 0 && hiddenCount == memberCount;
            return allHidden;
        }

        // ---------------------------------------------------------------------------------
        // The units, run through the oracle here and through the shipped helpers in
        // RecordingsTableGroupTreeHelperTests.
        // ---------------------------------------------------------------------------------

        internal interface IUnits
        {
            bool AllEnabled(HashSet<int> descendants, IReadOnlyList<Recording> committed, int memberCount);
            void ApplyEnabled(string groupName, HashSet<int> descendants, IReadOnlyList<Recording> committed, bool newEnabled);
            string Arrow(bool expanded);
            bool RenameKeys(RecordingsTableUI ui, string groupName, bool submitGrp, bool cancelGrp);
            string HeaderLabel(string treeConnector, string arrow, string groupLabel, int memberCount);
            bool HeaderClick(RecordingsTableUI ui, string groupName, IReadOnlyList<Recording> committed,
                bool expanded, int memberCount, float t);
            int ManageCell(bool hasAbsorbedRootMembers, bool canDisbandGroup);
            IEnumerable<int> LoopScope(HashSet<int> descendants, List<int> absorbedRootMembers);
            bool AnyWatched(int watchedIdx, HashSet<int> descendants, IReadOnlyList<Recording> committed);
            WatchResult Rotation(HashSet<int> descendants, IReadOnlyList<Recording> committed,
                Func<int, bool> isEligibleForWatch, string cursorRecId, string watchedDescendantRecId,
                out int nextTargetIdx);
            bool CanWatch(WatchResult rotation, int nextTargetIdx, bool anyDescendantWatched, out bool isToggleOffPress);
            bool Transition(Dictionary<string, bool> lastCanWatchByGroup,
                Dictionary<string, string> lastResolvedWatchTargetByGroup, string groupWatchKey,
                bool canWatch, string resolvedTargetId);
            string WatchTooltip(WatchResult rotation, bool isWatching, int nextTargetIdx,
                string watchedDescendantRecId, IReadOnlyList<Recording> committed);
            string AdvancePressKind(string cursorRecId, bool isWrap);
            bool AllHidden(HashSet<int> descendants, IReadOnlyList<Recording> committed, int memberCount);
        }

        internal sealed class OracleUnits : IUnits
        {
            public bool AllEnabled(HashSet<int> d, IReadOnlyList<Recording> c, int m) => OracleAllEnabled(d, c, m);
            public void ApplyEnabled(string g, HashSet<int> d, IReadOnlyList<Recording> c, bool e) => OracleApplyEnabled(g, d, c, e);
            public string Arrow(bool expanded) => OracleArrow(expanded);
            public bool RenameKeys(RecordingsTableUI ui, string g, bool s, bool x) => OracleRenameKeys(ui, g, s, x);
            public string HeaderLabel(string tc, string a, string l, int m) => OracleHeaderLabel(tc, a, l, m);
            public bool HeaderClick(RecordingsTableUI ui, string g, IReadOnlyList<Recording> c, bool e, int m, float t)
                => OracleHeaderClick(ui, g, c, e, m, t);
            public int ManageCell(bool a, bool d) => OracleManageCell(a, d);
            public IEnumerable<int> LoopScope(HashSet<int> d, List<int> a) => OracleLoopScope(d, a);
            public bool AnyWatched(int w, HashSet<int> d, IReadOnlyList<Recording> c) => OracleAnyWatched(w, d, c);
            public WatchResult Rotation(HashSet<int> d, IReadOnlyList<Recording> c, Func<int, bool> e, string cur,
                string w, out int n) => OracleRotation(d, c, e, cur, w, out n);
            public bool CanWatch(WatchResult r, int n, bool a, out bool t) => OracleCanWatch(r, n, a, out t);
            public bool Transition(Dictionary<string, bool> a, Dictionary<string, string> b, string k, bool cw, string fp)
                => OracleTransition(a, b, k, cw, fp);
            public string WatchTooltip(WatchResult r, bool w, int n, string wid, IReadOnlyList<Recording> c)
                => OracleWatchTooltip(r, w, n, wid, c);
            public string AdvancePressKind(string cur, bool wrap) => OracleAdvancePressKind(cur, wrap);
            public bool AllHidden(HashSet<int> d, IReadOnlyList<Recording> c, int m) => OracleAllHidden(d, c, m);
        }

        // ---------------------------------------------------------------------------------
        // Sweeps. Each builds its own fixtures on fresh stores, runs every branch of one
        // block and prints what the block returned and the state it left behind.
        // `startCapture` clears the log buffer right before the block runs.
        // ---------------------------------------------------------------------------------

        internal static readonly string[] SweepNames =
        {
            "AllEnabled", "ApplyEnabled", "Arrow", "RenameKeys", "HeaderLabel", "HeaderClick",
            "ManageCell", "LoopScope", "AnyWatched", "Rotation", "CanWatch", "Transition",
            "WatchTooltip", "AdvancePressKind", "AllHidden",
        };

        public static IEnumerable<object[]> Sweeps()
        {
            foreach (string n in SweepNames) yield return new object[] { n };
        }

        private static string B(bool b) => b ? "T" : "F";

        private static string Ints(IEnumerable<int> xs) =>
            xs == null ? "<null>" : string.Join(",", xs.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)));

        private static string R(WatchResult r) =>
            (r.NextRecordingId ?? "<null>") + "/" + r.Position.ToString(CultureInfo.InvariantCulture)
            + "/" + r.TotalEligible.ToString(CultureInfo.InvariantCulture)
            + "/" + B(r.IsToggleOff) + "/" + B(r.IsWrap);

        private static Recording Rec(string id, string name, double startUT)
        {
            return C.MakeRec(id, name, startUT, startUT + 10);
        }

        internal static string UiState(RecordingsTableUI ui)
        {
            Rect rr = Get<Rect>(ui, "activeRenameRect");
            return "renamingGroup=" + (Get<string>(ui, "renamingGroup") ?? "<null>")
                + " text=" + (Get<string>(ui, "renamingGroupText") ?? "<null>")
                + " focused=" + B(Get<bool>(ui, "renamingGroupFocused"))
                + " renamingRec=" + (Get<string>(ui, "renamingRecordingId") ?? "<null>")
                + " rect=" + rr.x.ToString("R", CultureInfo.InvariantCulture)
                + "," + rr.y.ToString("R", CultureInfo.InvariantCulture)
                + "," + rr.width.ToString("R", CultureInfo.InvariantCulture)
                + "," + rr.height.ToString("R", CultureInfo.InvariantCulture)
                + " lastClicked=" + (Get<string>(ui, "lastClickedGroup") ?? "<null>")
                + " lastTime=" + Get<float>(ui, "lastGroupClickTime").ToString("R", CultureInfo.InvariantCulture)
                + " expanded=[" + string.Join(",", Get<HashSet<string>>(ui, "expandedGroups")
                    .OrderBy(g => g, StringComparer.Ordinal)) + "]";
        }

        private static string Groups(IReadOnlyList<Recording> committed)
        {
            return string.Join(" ", committed.Select(r => r.RecordingId + ":"
                + (r.RecordingGroups == null ? "" : string.Join("+", r.RecordingGroups))));
        }

        private static void Logs(StringBuilder sb, List<string> logs)
        {
            foreach (string l in logs) sb.Append("  log ").Append(l).Append('\n');
        }

        internal static string RunSweep(string name, IUnits u, List<string> logs)
        {
            var sb = new StringBuilder();
            switch (name)
            {
                case "AllEnabled":
                case "AllHidden":
                {
                    bool hidden = name == "AllHidden";
                    var a = Rec("a", "A", 0);
                    var b = Rec("b", "B", 10);
                    var c = Rec("c", "C", 20);
                    C.Commit(a, b, c);
                    Action<bool, bool, bool> setFlags = (fa, fb, fc) =>
                    {
                        if (hidden) { a.Hidden = fa; b.Hidden = fb; c.Hidden = fc; }
                        else { a.PlaybackEnabled = fa; b.PlaybackEnabled = fb; c.PlaybackEnabled = fc; }
                    };
                    Func<HashSet<int>, int, bool> run = (d, m) => hidden
                        ? u.AllHidden(d, C.Committed, m)
                        : u.AllEnabled(d, C.Committed, m);
                    setFlags(true, true, true);
                    sb.Append("empty,m0=").Append(B(run(new HashSet<int>(), 0))).Append('\n');
                    sb.Append("all3,m3=").Append(B(run(new HashSet<int> { 0, 1, 2 }, 3))).Append('\n');
                    sb.Append("all3,m4=").Append(B(run(new HashSet<int> { 0, 1, 2 }, 4))).Append('\n');
                    sb.Append("all3,m0=").Append(B(run(new HashSet<int> { 0, 1, 2 }, 0))).Append('\n');
                    setFlags(true, false, true);
                    sb.Append("oneOff,m3=").Append(B(run(new HashSet<int> { 0, 1, 2 }, 3))).Append('\n');
                    sb.Append("subsetOn,m2=").Append(B(run(new HashSet<int> { 0, 2 }, 2))).Append('\n');
                    setFlags(false, false, false);
                    sb.Append("noneOn,m3=").Append(B(run(new HashSet<int> { 0, 1, 2 }, 3))).Append('\n');
                    break;
                }
                case "ApplyEnabled":
                {
                    var a = Rec("a", "Alpha", 0);
                    var b = Rec("b", "Beta", 10);
                    var c = Rec("c", "Gamma", 20);
                    C.Commit(a, b, c);
                    b.PlaybackEnabled = false;
                    logs.Clear();
                    u.ApplyEnabled("Munshots", new HashSet<int> { 0, 1 }, C.Committed, false);
                    sb.Append("off: ").Append(B(a.PlaybackEnabled)).Append(B(b.PlaybackEnabled)).Append(B(c.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.ApplyEnabled("Munshots", new HashSet<int> { 0, 1, 2 }, C.Committed, true);
                    sb.Append("on: ").Append(B(a.PlaybackEnabled)).Append(B(b.PlaybackEnabled)).Append(B(c.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.ApplyEnabled("Empty", new HashSet<int>(), C.Committed, true);
                    Logs(sb, logs);
                    break;
                }
                case "Arrow":
                    sb.Append(u.Arrow(true)).Append('|').Append(u.Arrow(false));
                    break;
                case "HeaderLabel":
                    sb.Append(u.HeaderLabel(null, "\u25b6", "Debris", 3)).Append('\n');
                    sb.Append(u.HeaderLabel("\u251c ", "\u25bc", "Kerbal X", 12)).Append('\n');
                    sb.Append(u.HeaderLabel("", "\u25b6", "", 0)).Append('\n');
                    break;
                case "RenameKeys":
                {
                    var a = Rec("a", "A", 0);
                    a.RecordingGroups = new List<string> { "Old" };
                    C.Commit(a);
                    var ui = C.MakeTable();
                    foreach (var keys in new[] { Tuple.Create(false, false), Tuple.Create(false, true),
                                 Tuple.Create(true, true), Tuple.Create(true, false) })
                    {
                        Set(ui, "renamingGroup", "Old");
                        Set(ui, "renamingGroupText", "  New Name ");
                        Set(ui, "activeRenameRect", new Rect(1f, 2f, 3f, 4f));
                        logs.Clear();
                        bool used = u.RenameKeys(ui, "Old", keys.Item1, keys.Item2);
                        sb.Append("submit=").Append(B(keys.Item1)).Append(" cancel=").Append(B(keys.Item2))
                            .Append(" used=").Append(B(used)).Append(' ').Append(UiState(ui))
                            .Append(" groups=").Append(Groups(C.Committed)).Append('\n');
                        Logs(sb, logs);
                    }
                    break;
                }
                case "HeaderClick":
                {
                    var a = Rec("a", "A", 0);
                    a.RecordingGroups = new List<string> { "Other" };
                    C.Commit(a);
                    var ui = C.MakeTable();
                    Action<string, bool, float> click = (label, expanded, t) =>
                    {
                        logs.Clear();
                        bool result = u.HeaderClick(ui, "G", C.Committed, expanded, 7, t);
                        sb.Append(label).Append(" -> ").Append(B(result)).Append(' ').Append(UiState(ui))
                            .Append(" groups=").Append(Groups(C.Committed)).Append('\n');
                        Logs(sb, logs);
                    };
                    // First click on a collapsed folder expands it.
                    click("first", false, 10f);
                    // A second click inside the threshold arms the rename; expansion holds.
                    click("double", true, 10.2f);
                    // The next click toggles again (lastClicked was cleared).
                    click("afterDouble", true, 10.3f);
                    // Outside the threshold: a plain toggle.
                    click("slow", false, 10.7f);
                    // Exactly at the threshold: not a double-click. 0.8f - 0.5f is exactly 0.3f
                    // in float (20.3f - 20f would be 0.2999992f, under the threshold).
                    Set(ui, "lastClickedGroup", "G");
                    Set(ui, "lastGroupClickTime", 0.5f);
                    click("atThreshold", true, 0.8f);
                    // A click on another folder's header is never a double-click.
                    Set(ui, "lastClickedGroup", "H");
                    Set(ui, "lastGroupClickTime", 30f);
                    click("otherGroup", false, 30.1f);
                    // A double-click commits a live recording rename and a live rename of
                    // another folder first.
                    Set(ui, "lastClickedGroup", "G");
                    Set(ui, "lastGroupClickTime", 40f);
                    Set(ui, "renamingRecordingId", "gone");
                    Set(ui, "renamingGroup", "Other");
                    Set(ui, "renamingGroupText", "Renamed");
                    Set(ui, "activeRenameRect", new Rect(5f, 6f, 7f, 8f));
                    click("doubleWithLiveRenames", true, 40.1f);
                    break;
                }
                case "ManageCell":
                    sb.Append(u.ManageCell(true, true)).Append(u.ManageCell(true, false))
                        .Append(u.ManageCell(false, true)).Append(u.ManageCell(false, false));
                    break;
                case "LoopScope":
                {
                    var d = new HashSet<int> { 3, 1 };
                    IEnumerable<int> s1 = u.LoopScope(d, null);
                    sb.Append("noAbsorb same=").Append(B(ReferenceEquals(s1, d))).Append(" [").Append(Ints(s1)).Append("]\n");
                    var absorbed = new List<int> { 0, 1 };
                    IEnumerable<int> s2 = u.LoopScope(d, absorbed);
                    sb.Append("absorb same=").Append(B(ReferenceEquals(s2, d))).Append(" type=").Append(s2.GetType().Name)
                        .Append(" [").Append(Ints(s2)).Append("] desc=[").Append(Ints(d))
                        .Append("] absorbed=[").Append(Ints(absorbed)).Append("]\n");
                    IEnumerable<int> s3 = u.LoopScope(new HashSet<int>(), new List<int>());
                    sb.Append("bothEmpty type=").Append(s3.GetType().Name).Append(" [").Append(Ints(s3)).Append("]\n");
                    break;
                }
                case "AnyWatched":
                {
                    var list = new List<Recording> { Rec("a", "A", 0), null, Rec("c", "C", 20) };
                    var d = new HashSet<int> { 0, 1, 5 };
                    foreach (int w in new[] { -1, 0, 1, 2, 5 })
                        sb.Append(w.ToString(CultureInfo.InvariantCulture)).Append('=')
                            .Append(B(u.AnyWatched(w, d, list))).Append(' ');
                    break;
                }
                case "Rotation":
                {
                    var list = new List<Recording>
                    {
                        Rec("r0", "Zero", 30), Rec("r1", "One", 10), Rec("r2", "Two", 20),
                        Rec("r3", "Three", 10), Rec(null, "NoId", 5),
                    };
                    var all = new HashSet<int> { 0, 1, 2, 3, 4 };
                    Func<int, bool> every = idx => true;
                    Func<int, bool> none = idx => false;
                    Func<int, bool> onlyTwo = idx => idx == 2;
                    var cases = new[]
                    {
                        Tuple.Create("empty-desc", new HashSet<int>(), every, (string)null, (string)null),
                        Tuple.Create("none-eligible", all, none, (string)null, (string)null),
                        Tuple.Create("first-press", all, every, (string)null, (string)null),
                        Tuple.Create("advance", all, every, "r1", (string)null),
                        Tuple.Create("advance-skips-watched", all, every, "r1", "r3"),
                        Tuple.Create("wrap", all, every, "r0", (string)null),
                        Tuple.Create("unknown-cursor", all, every, "zz", (string)null),
                        Tuple.Create("single-not-watched", all, onlyTwo, (string)null, (string)null),
                        Tuple.Create("single-watched", all, onlyTwo, "r2", "r2"),
                        Tuple.Create("out-of-range-desc", new HashSet<int> { 9, 2 }, every, (string)null, (string)null),
                    };
                    foreach (var k in cases)
                    {
                        int next;
                        WatchResult r = u.Rotation(k.Item2, list, k.Item3, k.Item4, k.Item5, out next);
                        sb.Append(k.Item1).Append(" -> ").Append(R(r)).Append(" next=")
                            .Append(next.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                    // The rotation's recording left the list between the eligibility read and
                    // the index lookup: an empty rotation, no target.
                    Func<int, bool> replaceTwo = idx =>
                    {
                        if (idx == 2) list[2] = Rec("other", "Other", 20);
                        return idx == 2;
                    };
                    int gone;
                    WatchResult removed = u.Rotation(new HashSet<int> { 2 }, list, replaceTwo, null, null, out gone);
                    sb.Append("removed-between-draws -> ").Append(R(removed)).Append(" next=")
                        .Append(gone.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                }
                case "CanWatch":
                {
                    var results = new[]
                    {
                        WatchResult.Empty,
                        new WatchResult("r1", 1, 2, false, false),
                        new WatchResult("r1", 2, 2, false, true),
                        new WatchResult("r2", 1, 1, true, false),
                    };
                    foreach (WatchResult r in results)
                        foreach (int next in new[] { -1, 0, 3 })
                            foreach (bool any in new[] { false, true })
                            {
                                bool off;
                                bool can = u.CanWatch(r, next, any, out off);
                                sb.Append(R(r)).Append(" next=").Append(next.ToString(CultureInfo.InvariantCulture))
                                    .Append(" any=").Append(B(any)).Append(" -> can=").Append(B(can))
                                    .Append(" off=").Append(B(off)).Append('\n');
                            }
                    break;
                }
                case "Transition":
                {
                    var a = new Dictionary<string, bool>();
                    var b = new Dictionary<string, string>();
                    Action<string, string, bool, string> step = (label, key, can, fp) =>
                    {
                        bool log = u.Transition(a, b, key, can, fp);
                        sb.Append(label).Append(" -> ").Append(B(log)).Append(" can=[")
                            .Append(string.Join(",", a.OrderBy(p => p.Key, StringComparer.Ordinal)
                                .Select(p => p.Key + ":" + B(p.Value))))
                            .Append("] fp=[").Append(string.Join(",", b.OrderBy(p => p.Key, StringComparer.Ordinal)
                                .Select(p => p.Key + ":" + (p.Value ?? "<null>")))).Append("]\n");
                    };
                    step("first", "G/r0", true, "r0,r1");
                    step("same", "G/r0", true, "r0,r1");
                    step("canFlips", "G/r0", false, "r0,r1");
                    step("setChanges", "G/r0", false, "r0");
                    step("setEmpty", "G/r0", false, "");
                    step("setNull", "G/r0", false, null);
                    step("nullAgain", "G/r0", false, null);
                    step("otherKey", "H/r5", false, null);
                    // A key present in only one of the two caches still logs.
                    a["J/r9"] = true;
                    step("halfCached", "J/r9", true, "r9");
                    b["K/r8"] = "r8";
                    step("otherHalfCached", "K/r8", true, "r8");
                    break;
                }
                case "WatchTooltip":
                {
                    var list = new List<Recording> { Rec("r0", "Kerbal X", 0), Rec("r1", "Probe", 10) };
                    var empty = WatchResult.Empty;
                    var toR1 = new WatchResult("r1", 2, 2, false, false);
                    var toR0 = new WatchResult("r0", 1, 2, false, true);
                    var off = new WatchResult("r1", 1, 1, true, false);
                    var cases = new[]
                    {
                        Tuple.Create("none", empty, false, -1, (string)null),
                        Tuple.Create("none-watching", empty, true, -1, "r1"),
                        Tuple.Create("switch", toR1, true, 1, "r0"),
                        Tuple.Create("watching-same-next", toR1, true, 1, "r1"),
                        Tuple.Create("toggle-off", off, true, 1, "r1"),
                        Tuple.Create("toggle-off-not-watching", off, false, 1, (string)null),
                        Tuple.Create("enter", toR0, false, 0, (string)null),
                        Tuple.Create("no-target", toR0, false, -1, (string)null),
                        Tuple.Create("no-target-watching", toR0, true, -1, "r1"),
                    };
                    foreach (var k in cases)
                        sb.Append(k.Item1).Append(" -> ")
                            .Append(u.WatchTooltip(k.Item2, k.Item3, k.Item4, k.Item5, list)).Append('\n');
                    break;
                }
                case "AdvancePressKind":
                    sb.Append(u.AdvancePressKind(null, false)).Append(',').Append(u.AdvancePressKind(null, true))
                        .Append(',').Append(u.AdvancePressKind("", true)).Append(',').Append(u.AdvancePressKind("r1", true))
                        .Append(',').Append(u.AdvancePressKind("r1", false));
                    break;
                default:
                    throw new ArgumentException("unknown sweep " + name);
            }
            return sb.ToString();
        }

        internal string Run(string name, IUnits u)
        {
            C.ResetAll();
            logLines.Clear();
            return RunSweep(name, u, logLines);
        }

        internal static readonly Dictionary<string, string> Goldens = new Dictionary<string, string>
        {
            {
                "AllEnabled",
                "empty,m0=F\n"
                + "all3,m3=T\n"
                + "all3,m4=F\n"
                + "all3,m0=F\n"
                + "oneOff,m3=F\n"
                + "subsetOn,m2=T\n"
                + "noneOn,m3=F\n"
            },
            {
                "ApplyEnabled",
                "off: FFT\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback disabled: rec=a vessel=\"Alpha\"\n"
                + "  log [Parsek][INFO][UI] Set playback enabled for group 'Munshots': enabled=False\n"
                + "on: TTT\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback enabled: rec=a vessel=\"Alpha\"\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback enabled: rec=b vessel=\"Beta\"\n"
                + "  log [Parsek][INFO][UI] Set playback enabled for group 'Munshots': enabled=True\n"
                + "  log [Parsek][INFO][UI] Set playback enabled for group 'Empty': enabled=True\n"
            },
            {
                "Arrow",
                "\u25bc|\u25b6"
            },
            {
                "RenameKeys",
                "submit=F cancel=F used=F renamingGroup=Old text=  New Name  focused=F renamingRec=<null> rect=1,2,3,4 lastClicked=<null> lastTime=0 expanded=[] groups=a:Old\n"
                + "submit=F cancel=T used=T renamingGroup=<null> text=  New Name  focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=<null> lastTime=0 expanded=[] groups=a:Old\n"
                + "submit=T cancel=T used=T renamingGroup=<null> text=  New Name  focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=<null> lastTime=0 expanded=[] groups=a:New Name\n"
                + "  log [Parsek][INFO][RecordingStore] RenameGroup: 'Old' \u2192 'New Name' (1 recordings updated)\n"
                + "  log [Parsek][INFO][UI] Group 'Old' renamed to 'New Name'\n"
                + "submit=T cancel=F used=T renamingGroup=<null> text=  New Name  focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=<null> lastTime=0 expanded=[] groups=a:New Name\n"
                + "  log [Parsek][WARN][RecordingStore] RenameGroup: cannot rename 'Old' to 'New Name' \u2014 name already exists\n"
                + "  log [Parsek][WARN][UI] Group rename rejected: 'New Name' already exists\n"
            },
            {
                "HeaderLabel",
                "\u25b6 Debris (3)\n"
                + "\u251c \u25bc Kerbal X (12)\n"
                + "\u25b6  (0)\n"
            },
            {
                "HeaderClick",
                "first -> T renamingGroup=<null> text= focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=G lastTime=10 expanded=[G] groups=a:Other\n"
                + "  log [Parsek][VERBOSE][UI] Group 'G' expanded (7 recordings)\n"
                + "double -> T renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=<null> lastTime=10 expanded=[G] groups=a:Other\n"
                + "afterDouble -> F renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=G lastTime=10.3 expanded=[] groups=a:Other\n"
                + "  log [Parsek][VERBOSE][UI] Group 'G' collapsed (7 recordings)\n"
                + "slow -> T renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=G lastTime=10.7 expanded=[G] groups=a:Other\n"
                + "  log [Parsek][VERBOSE][UI] Group 'G' expanded (7 recordings)\n"
                + "atThreshold -> F renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=G lastTime=0.8 expanded=[] groups=a:Other\n"
                + "  log [Parsek][VERBOSE][UI] Group 'G' collapsed (7 recordings)\n"
                + "otherGroup -> T renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=G lastTime=30.1 expanded=[G] groups=a:Other\n"
                + "  log [Parsek][VERBOSE][UI] Group 'G' expanded (7 recordings)\n"
                + "doubleWithLiveRenames -> T renamingGroup=G text=G focused=F renamingRec=<null> rect=0,0,0,0 lastClicked=<null> lastTime=40 expanded=[G] groups=a:Renamed\n"
                + "  log [Parsek][WARN][UI] Recording rename dropped: id=gone is no longer in the committed list\n"
                + "  log [Parsek][INFO][RecordingStore] RenameGroup: 'Other' \u2192 'Renamed' (1 recordings updated)\n"
                + "  log [Parsek][INFO][UI] Group 'Other' renamed to 'Renamed'\n"
            },
            {
                "ManageCell",
                "0012"
            },
            {
                "LoopScope",
                "noAbsorb same=T [1,3]\n"
                + "absorb same=F type=HashSet`1 [0,1,3] desc=[1,3] absorbed=[0,1]\n"
                + "bothEmpty type=HashSet`1 []\n"
            },
            {
                "AnyWatched",
                "-1=F 0=T 1=F 2=F 5=F "
            },
            {
                "Rotation",
                "empty-desc -> <null>/0/0/F/F next=-1\n"
                + "none-eligible -> <null>/0/0/F/F next=-1\n"
                + "first-press -> r1/1/4/F/F next=1\n"
                + "advance -> r3/2/4/F/F next=3\n"
                + "advance-skips-watched -> r2/3/4/F/F next=2\n"
                + "wrap -> r1/1/4/F/T next=1\n"
                + "unknown-cursor -> r1/1/4/F/F next=1\n"
                + "single-not-watched -> r2/1/1/F/F next=2\n"
                + "single-watched -> r2/1/1/T/F next=2\n"
                + "out-of-range-desc -> r2/1/1/F/F next=2\n"
                + "removed-between-draws -> <null>/0/0/F/F next=-1\n"
            },
            {
                "CanWatch",
                "<null>/0/0/F/F next=-1 any=F -> can=F off=F\n"
                + "<null>/0/0/F/F next=-1 any=T -> can=F off=F\n"
                + "<null>/0/0/F/F next=0 any=F -> can=F off=F\n"
                + "<null>/0/0/F/F next=0 any=T -> can=F off=F\n"
                + "<null>/0/0/F/F next=3 any=F -> can=F off=F\n"
                + "<null>/0/0/F/F next=3 any=T -> can=F off=F\n"
                + "r1/1/2/F/F next=-1 any=F -> can=F off=F\n"
                + "r1/1/2/F/F next=-1 any=T -> can=F off=F\n"
                + "r1/1/2/F/F next=0 any=F -> can=T off=F\n"
                + "r1/1/2/F/F next=0 any=T -> can=T off=F\n"
                + "r1/1/2/F/F next=3 any=F -> can=T off=F\n"
                + "r1/1/2/F/F next=3 any=T -> can=T off=F\n"
                + "r1/2/2/F/T next=-1 any=F -> can=F off=F\n"
                + "r1/2/2/F/T next=-1 any=T -> can=F off=F\n"
                + "r1/2/2/F/T next=0 any=F -> can=T off=F\n"
                + "r1/2/2/F/T next=0 any=T -> can=T off=F\n"
                + "r1/2/2/F/T next=3 any=F -> can=T off=F\n"
                + "r1/2/2/F/T next=3 any=T -> can=T off=F\n"
                + "r2/1/1/T/F next=-1 any=F -> can=F off=F\n"
                + "r2/1/1/T/F next=-1 any=T -> can=F off=T\n"
                + "r2/1/1/T/F next=0 any=F -> can=F off=F\n"
                + "r2/1/1/T/F next=0 any=T -> can=F off=T\n"
                + "r2/1/1/T/F next=3 any=F -> can=F off=F\n"
                + "r2/1/1/T/F next=3 any=T -> can=F off=T\n"
            },
            {
                "Transition",
                "first -> T can=[G/r0:T] fp=[G/r0:r0,r1]\n"
                + "same -> F can=[G/r0:T] fp=[G/r0:r0,r1]\n"
                + "canFlips -> T can=[G/r0:F] fp=[G/r0:r0,r1]\n"
                + "setChanges -> T can=[G/r0:F] fp=[G/r0:r0]\n"
                + "setEmpty -> T can=[G/r0:F] fp=[G/r0:]\n"
                + "setNull -> T can=[G/r0:F] fp=[G/r0:<null>]\n"
                + "nullAgain -> F can=[G/r0:F] fp=[G/r0:<null>]\n"
                + "otherKey -> T can=[G/r0:F,H/r5:F] fp=[G/r0:<null>,H/r5:<null>]\n"
                + "halfCached -> T can=[G/r0:F,H/r5:F,J/r9:T] fp=[G/r0:<null>,H/r5:<null>,J/r9:r9]\n"
                + "otherHalfCached -> T can=[G/r0:F,H/r5:F,J/r9:T,K/r8:T] fp=[G/r0:<null>,H/r5:<null>,J/r9:r9,K/r8:r8]\n"
            },
            {
                "WatchTooltip",
                "none -> no watchable vessels in this group\n"
                + "none-watching -> no watchable vessels in this group\n"
                + "switch -> switch to Probe\n"
                + "watching-same-next -> enter watch on Probe\n"
                + "toggle-off -> exit watch (no other watchable vessels)\n"
                + "toggle-off-not-watching -> enter watch on Probe\n"
                + "enter -> enter watch on Kerbal X\n"
                + "no-target -> No active ghost - recording is in the past/future or has no trajectory points\n"
                + "no-target-watching -> Exit watch mode\n"
            },
            {
                "AdvancePressKind",
                "enter,enter,enter,wrap,advance"
            },
            {
                "AllHidden",
                "empty,m0=F\n"
                + "all3,m3=T\n"
                + "all3,m4=F\n"
                + "all3,m0=F\n"
                + "oneOff,m3=F\n"
                + "subsetOn,m2=T\n"
                + "noneOn,m3=F\n"
            },
        };

        [Theory]
        [MemberData(nameof(Sweeps))]
        public void Golden_OriginalBlock(string name)
        {
            string actual = Run(name, new OracleUnits());
            Assert.True(Goldens[name] == actual,
                name + ": golden differs; the original block prints\n<<<" + actual + ">>>");
        }
    }
}
