using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using static Parsek.RecordingsTableUI;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization goldens for four blocks of the Recordings tab's IMGUI draw methods
    /// that hold no GUI call: the folder display-block build with its launched-vessel
    /// absorption and the folder Loop / Archive writes in <c>DrawGroupTree</c>, and the row
    /// Archive write in <c>DrawRecordingRow</c>. The draw methods cannot run headless, so each
    /// block is pinned through an ORACLE: the original block copied verbatim from the source
    /// (whole-line comments dropped, non-ASCII characters in literals written as \u escapes,
    /// the two private instance calls routed through reflection on the same table), swept over
    /// every branch. A golden digests the full post-call state: every committed recording's
    /// Hidden / Loop / loop range / playback flag, the hidden-group set, every log line, every
    /// screen message and the block's own outputs.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableClickHandlerCharacterizationTests : IDisposable
    {
        internal readonly List<string> logLines = new List<string>();
        internal readonly List<string> screenMessages = new List<string>();

        public RecordingsTableClickHandlerCharacterizationTests()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) =>
                screenMessages.Add(msg + "|" + dur.ToString("R", CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ResetAll();
        }

        // Also clears the log rate limits: the classifier's per-recording lines are
        // VerboseRateLimited, so a second run in one test (oracle, then the shipped helper)
        // would otherwise lose them and differ for that reason alone.
        internal static void ResetAll()
        {
            ParsekLog.ResetRateLimitsForTesting();
            ParsekUI.ResetUiComplexityModeForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            MissionStore.ResetForTesting();
            Ledger.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------------------------------------------------------------------------------
        // The four oracles: the ORIGINAL blocks, verbatim.
        // ---------------------------------------------------------------------------------

        internal static List<GroupDisplayBlock> OracleFolderDisplayBlocks(RecordingsTableUI ui,
            string groupName, IReadOnlyList<Recording> committed,
            Dictionary<string, List<int>> grpToRecs, Dictionary<string, List<int>> chainToRecs,
            out List<int> absorbedRootMembersOut)
        {
            List<int> directMembers;
            grpToRecs.TryGetValue(groupName, out directMembers);
            List<GroupDisplayBlock> displayBlocks = directMembers != null
                ? BuildGroupDisplayBlocks(groupName, directMembers, committed, chainToRecs)
                : null;
            List<int> absorbedRootMembers = null;
            if (displayBlocks != null)
            {
                RecordingTree missionTree = MissionGroupLink.FindTreeByRootGroup(groupName);
                string rootBlockKey = missionTree != null
                    ? ResolveRootVesselBlockKey(groupName, ResolveTreeRootRecording(missionTree))
                    : null;
                int absorbedIdx = FindRootVesselBlockIndex(displayBlocks, rootBlockKey);
                if (absorbedIdx >= 0)
                {
                    absorbedRootMembers = displayBlocks[absorbedIdx].Members;
                    displayBlocks = FlattenAbsorbedBlock(displayBlocks, absorbedIdx, groupName);
                }
                LogAbsorption(ui, groupName, rootBlockKey, absorbedRootMembers);
            }
            absorbedRootMembersOut = absorbedRootMembers;
            return displayBlocks;
        }

        internal static void OracleGroupLoop(string groupName, bool newLoop, bool grpRouteBound,
            IReadOnlyList<Recording> committed, HashSet<int> descendants,
            IEnumerable<int> grpLoopScope, List<int> absorbedRootMembers)
        {
            if (grpRouteBound)
            {
                ParsekLog.Info("RouteGuard",
                    $"Recordings-tab group Loop blocked for '{groupName}' (a descendant is on a route-bound tree); request={newLoop} ignored");
            }
            else if (absorbedRootMembers != null)
            {
                SplitAbsorbedLoopWrite(grpLoopScope, absorbedRootMembers,
                    out List<int> withAutoRange, out List<int> withoutAutoRange);
                int rootWritten = BulkSetLoopPlayback(
                    committed, withAutoRange, newLoop, applyAutoRange: true);
                int otherWritten = BulkSetLoopPlayback(
                    committed, withoutAutoRange, newLoop, applyAutoRange: false);
                ParsekLog.Info("UI",
                    $"Group '{groupName}' loop set to {newLoop} "
                    + $"(launched vessel {rootWritten.ToString(CultureInfo.InvariantCulture)} "
                    + "recordings with auto loop range, other "
                    + $"{otherWritten.ToString(CultureInfo.InvariantCulture)} without)");
            }
            else
            {
                int written = BulkSetLoopPlayback(committed, descendants, newLoop, applyAutoRange: false);
                ParsekLog.Info("UI", $"Group '{groupName}' loop set to {newLoop} ({written} recordings)");
            }
        }

        internal static void OracleGroupArchive(RecordingsTableUI ui, string groupName,
            HashSet<int> descendants, IReadOnlyList<Recording> committed, int memberCount,
            bool newAllHidden)
        {
            int unfinishedCount = 0;
            if (newAllHidden)
            {
                foreach (int idx in descendants)
                    if (EffectiveState.IsUnfinishedFlight(committed[idx]))
                        unfinishedCount++;
            }

            if (IsArchiveRefusedForUnfinishedFlight(newAllHidden, unfinishedCount > 0))
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Group hide-all refused for group '{groupName}': "
                    + $"{unfinishedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} of "
                    + $"{memberCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + "member(s) are Unfinished Flights; rewind access must remain "
                    + "visible (design \u00a77.33)");
                ParsekLog.ScreenMessage(
                    BuildGroupArchiveRefusedMessage(groupName, unfinishedCount), 4f);
            }
            else
            {
                foreach (int idx in descendants)
                    committed[idx].Hidden = newAllHidden;
                if (newAllHidden)
                    GroupHierarchyStore.AddHiddenGroup(groupName);
                else
                    GroupHierarchyStore.RemoveHiddenGroup(groupName);
                NotifyTimeline(ui);
                ParsekLog.Info("UI",
                    $"Group '{groupName}' hide-all={newAllHidden} ({memberCount} recordings)");
            }
        }

        internal static void OracleRowArchive(RecordingsTableUI ui, Recording rec, bool hidden)
        {
            if (IsArchiveRefusedForUnfinishedFlight(
                    hidden, EffectiveState.IsUnfinishedFlight(rec)))
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Hide refused for Unfinished Flight rec={rec.RecordingId ?? "<no-id>"} " +
                    $"vessel='{rec.VesselName}': rewind access must remain visible (design \u00a77.33)");
                ParsekLog.ScreenMessage(
                    $"Cannot hide '{rec.VesselName}' \u2014 it is an Unfinished Flight. " +
                    "Re-fly the rewind point or merge as Immutable to clear it from the list.",
                    4f);
            }
            else
            {
                rec.Hidden = hidden;
                NotifyTimeline(ui);
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' hidden={hidden}");
            }
        }

        private static readonly MethodInfo NotifyTimelineMethod = typeof(RecordingsTableUI)
            .GetMethod("NotifyTimelineOfArchiveChange", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo LogAbsorptionMethod = typeof(RecordingsTableUI)
            .GetMethod("LogRootBlockAbsorptionTransition", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void NotifyTimeline(RecordingsTableUI ui)
        {
            NotifyTimelineMethod.Invoke(ui, null);
        }

        private static void LogAbsorption(RecordingsTableUI ui, string groupName,
            string rootBlockKey, List<int> absorbedMembers)
        {
            LogAbsorptionMethod.Invoke(ui, new object[] { groupName, rootBlockKey, absorbedMembers });
        }

        // ---------------------------------------------------------------------------------
        // Fixtures
        // ---------------------------------------------------------------------------------

        internal static Recording MakeRec(string id, string name, double startUT = 0, double endUT = 100)
        {
            var r = new Recording { RecordingId = id, VesselName = name };
            r.Points.Add(new TrajectoryPoint { ut = startUT });
            r.Points.Add(new TrajectoryPoint { ut = endUT });
            return r;
        }

        // Loopable (launch site) with a propulsive middle, so an auto loop range narrows.
        internal static Recording MakeLoopable(string id, string name)
        {
            var r = MakeRec(id, name, 0, 300);
            r.LaunchSiteName = "LaunchPad";
            r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.SurfaceStationary, startUT = 0, endUT = 100 });
            r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 300 });
            return r;
        }

        internal static Recording MakeTreeRec(string id, string name, string group, string treeId,
            uint pid, double startUT, double endUT)
        {
            var r = MakeRec(id, name, startUT, endUT);
            r.TreeId = treeId;
            r.VesselPersistentId = pid;
            r.RecordingGroups = new List<string> { group };
            return r;
        }

        // An open (crashed, CommittedProvisional) Unfinished Flight on its own rewind point
        // slot; the RP list is installed by InstallRewindPoints.
        internal static Recording MakeUnfinished(string id, string name, string bpId)
        {
            var r = MakeRec(id, name);
            r.MergeState = MergeState.CommittedProvisional;
            r.TerminalStateValue = TerminalState.Destroyed;
            r.ParentBranchPointId = bpId;
            return r;
        }

        internal static void InstallRewindPoints(params Recording[] unfinished)
        {
            var rps = new List<RewindPoint>();
            for (int i = 0; i < unfinished.Length; i++)
            {
                rps.Add(new RewindPoint
                {
                    RewindPointId = "rp-" + unfinished[i].RecordingId,
                    BranchPointId = unfinished[i].ParentBranchPointId,
                    ChildSlots = new List<ChildSlot>
                    {
                        new ChildSlot
                        {
                            SlotIndex = 0,
                            OriginChildRecordingId = unfinished[i].RecordingId,
                            Controllable = true,
                        },
                    },
                    UT = 100.0,
                    SessionProvisional = false,
                });
            }
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RewindPoints = rps,
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
            });
        }

        internal static void Commit(params Recording[] recs)
        {
            for (int i = 0; i < recs.Length; i++)
                RecordingStore.AddCommittedInternal(recs[i]);
        }

        internal static IReadOnlyList<Recording> Committed => RecordingStore.CommittedRecordings;

        // A table whose parent is a real headless ParsekUI, so the Timeline invalidation the
        // Archive writes trigger is observable as its own log line.
        internal static RecordingsTableUI MakeTable()
        {
            return new ParsekUI(UIMode.KSC).GetRecordingsTableUI();
        }

        // ---------------------------------------------------------------------------------
        // Scenarios. Each builds its fixture, runs the block through `run` (the oracle here,
        // the extracted helper in the direct tests) and returns the block's own outputs.
        // ---------------------------------------------------------------------------------

        internal interface IBlocks
        {
            List<GroupDisplayBlock> FolderDisplayBlocks(RecordingsTableUI ui, string groupName,
                IReadOnlyList<Recording> committed, Dictionary<string, List<int>> grpToRecs,
                Dictionary<string, List<int>> chainToRecs, out List<int> absorbedRootMembers);
            void GroupLoop(string groupName, bool newLoop, bool grpRouteBound,
                IReadOnlyList<Recording> committed, HashSet<int> descendants,
                IEnumerable<int> grpLoopScope, List<int> absorbedRootMembers);
            void GroupArchive(RecordingsTableUI ui, string groupName, HashSet<int> descendants,
                IReadOnlyList<Recording> committed, int memberCount, bool newAllHidden);
            void RowArchive(RecordingsTableUI ui, Recording rec, bool hidden);
        }

        internal sealed class OracleBlocks : IBlocks
        {
            public List<GroupDisplayBlock> FolderDisplayBlocks(RecordingsTableUI ui, string groupName,
                IReadOnlyList<Recording> committed, Dictionary<string, List<int>> grpToRecs,
                Dictionary<string, List<int>> chainToRecs, out List<int> absorbedRootMembers)
                => OracleFolderDisplayBlocks(ui, groupName, committed, grpToRecs, chainToRecs,
                    out absorbedRootMembers);
            public void GroupLoop(string groupName, bool newLoop, bool grpRouteBound,
                IReadOnlyList<Recording> committed, HashSet<int> descendants,
                IEnumerable<int> grpLoopScope, List<int> absorbedRootMembers)
                => OracleGroupLoop(groupName, newLoop, grpRouteBound, committed, descendants,
                    grpLoopScope, absorbedRootMembers);
            public void GroupArchive(RecordingsTableUI ui, string groupName, HashSet<int> descendants,
                IReadOnlyList<Recording> committed, int memberCount, bool newAllHidden)
                => OracleGroupArchive(ui, groupName, descendants, committed, memberCount, newAllHidden);
            public void RowArchive(RecordingsTableUI ui, Recording rec, bool hidden)
                => OracleRowArchive(ui, rec, hidden);
        }

        internal static readonly string[] ScenarioNames =
        {
            "Folder_NoDirectMembers",
            "Folder_PlainGroup_NoMissionTree",
            "Folder_MissionTree_AbsorbsRootVessel",
            "Folder_MissionTree_RootHasOneSegment",
            "Folder_AbsorbTwice_ThenRelease",
            "GroupLoop_RouteBound_Blocked",
            "GroupLoop_Absorbed_On",
            "GroupLoop_Absorbed_Off",
            "GroupLoop_Plain_On",
            "GroupLoop_Plain_Off",
            "GroupArchive_HideAll",
            "GroupArchive_UnhideAll",
            "GroupArchive_HideRefused_OneUnfinished",
            "GroupArchive_HideRefused_TwoUnfinished",
            "GroupArchive_UnhideWithUnfinished_Allowed",
            "GroupArchive_EmptyFolder_Hide",
            "RowArchive_Hide",
            "RowArchive_Unhide",
            "RowArchive_HideRefused_Unfinished",
            "RowArchive_UnhideUnfinished_Allowed",
        };

        public static IEnumerable<object[]> Scenarios()
        {
            foreach (string n in ScenarioNames) yield return new object[] { n };
        }

        internal static string RunScenario(string name, IBlocks run, Action startCapture)
        {
            var output = new StringBuilder();
            var grpToRecs = new Dictionary<string, List<int>>();
            var chain = new Dictionary<string, List<int>>();
            switch (name)
            {
                case "Folder_NoDirectMembers":
                {
                    Commit(MakeRec("a", "A"));
                    var ui = MakeTable();
                    startCapture();
                    AppendBlocks(output, run.FolderDisplayBlocks(ui, "G", Committed, grpToRecs, chain, out var abs), abs);
                    break;
                }
                case "Folder_PlainGroup_NoMissionTree":
                {
                    Commit(MakeTreeRec("a", "Hopper", "G", "t1", 5, 0, 10),
                        MakeTreeRec("b", "Hopper", "G", "t1", 5, 10, 20),
                        MakeRec("c", "Lone"));
                    grpToRecs["G"] = new List<int> { 0, 1, 2 };
                    var ui = MakeTable();
                    startCapture();
                    AppendBlocks(output, run.FolderDisplayBlocks(ui, "G", Committed, grpToRecs, chain, out var abs), abs);
                    break;
                }
                case "Folder_MissionTree_AbsorbsRootVessel":
                case "Folder_AbsorbTwice_ThenRelease":
                {
                    var r0 = MakeTreeRec("kx-0", "Kerbal X", "Kerbal X", "tree-kx", 42, 10, 20);
                    Commit(r0,
                        MakeTreeRec("kx-1", "Kerbal X", "Kerbal X", "tree-kx", 42, 20, 30),
                        MakeTreeRec("pr-0", "Kerbal X Probe", "Kerbal X", "tree-kx", 77, 25, 40),
                        MakeTreeRec("pr-1", "Kerbal X Probe", "Kerbal X", "tree-kx", 77, 40, 50),
                        MakeTreeRec("kx-2", "Kerbal X", "Kerbal X", "tree-kx", 42, 30, 60));
                    var tree = new RecordingTree
                    {
                        Id = "tree-kx",
                        TreeName = "Kerbal X",
                        RootRecordingId = "kx-0",
                        AutoGeneratedRootGroupName = "Kerbal X",
                    };
                    tree.Recordings["kx-0"] = r0;
                    RecordingStore.AddCommittedTreeForTesting(tree);
                    grpToRecs["Kerbal X"] = new List<int> { 0, 1, 2, 3, 4 };
                    var ui = MakeTable();
                    startCapture();
                    AppendBlocks(output, run.FolderDisplayBlocks(ui, "Kerbal X", Committed, grpToRecs, chain, out var abs), abs);
                    if (name == "Folder_AbsorbTwice_ThenRelease")
                    {
                        // Same result again: the transition log stays silent.
                        AppendBlocks(output, run.FolderDisplayBlocks(ui, "Kerbal X", Committed, grpToRecs, chain, out abs), abs);
                        // The root vessel's block drops to one segment: no longer absorbed.
                        grpToRecs["Kerbal X"] = new List<int> { 0, 2, 3 };
                        AppendBlocks(output, run.FolderDisplayBlocks(ui, "Kerbal X", Committed, grpToRecs, chain, out abs), abs);
                    }
                    break;
                }
                case "Folder_MissionTree_RootHasOneSegment":
                {
                    var r0 = MakeTreeRec("h-0", "Hopper", "Hopper", "tree-h", 5, 10, 20);
                    Commit(r0,
                        MakeTreeRec("d-0", "Hopper Debris", "Hopper", "tree-h", 6, 12, 30),
                        MakeTreeRec("d-1", "Hopper Debris", "Hopper", "tree-h", 6, 30, 40));
                    var tree = new RecordingTree
                    {
                        Id = "tree-h",
                        TreeName = "Hopper",
                        RootRecordingId = "h-0",
                        AutoGeneratedRootGroupName = "Hopper",
                    };
                    tree.Recordings["h-0"] = r0;
                    RecordingStore.AddCommittedTreeForTesting(tree);
                    grpToRecs["Hopper"] = new List<int> { 0, 1, 2 };
                    var ui = MakeTable();
                    startCapture();
                    AppendBlocks(output, run.FolderDisplayBlocks(ui, "Hopper", Committed, grpToRecs, chain, out var abs), abs);
                    break;
                }
                case "GroupLoop_RouteBound_Blocked":
                {
                    Commit(MakeLoopable("a", "A"), MakeLoopable("b", "B"));
                    var desc = new HashSet<int> { 0, 1 };
                    startCapture();
                    run.GroupLoop("G", true, true, Committed, desc, desc, null);
                    break;
                }
                case "GroupLoop_Absorbed_On":
                case "GroupLoop_Absorbed_Off":
                {
                    bool on = name.EndsWith("_On", StringComparison.Ordinal);
                    var root0 = MakeLoopable("r0", "Root");
                    var root1 = MakeLoopable("r1", "Root");
                    var other = MakeLoopable("o", "Other");
                    var debris = MakeLoopable("d", "Debris");
                    debris.IsDebris = true;
                    Commit(root0, root1, other, debris);
                    if (!on)
                    {
                        foreach (var r in new[] { root0, root1, other })
                        {
                            r.LoopPlayback = true;
                            r.LoopStartUT = 100;
                            r.LoopEndUT = 200;
                        }
                    }
                    var desc = new HashSet<int> { 2, 3 };
                    var absorbed = new List<int> { 0, 1 };
                    var scope = new HashSet<int>(desc);
                    scope.UnionWith(absorbed);
                    startCapture();
                    run.GroupLoop("Mission", on, false, Committed, desc, scope, absorbed);
                    break;
                }
                case "GroupLoop_Plain_On":
                case "GroupLoop_Plain_Off":
                {
                    bool on = name.EndsWith("_On", StringComparison.Ordinal);
                    var a = MakeLoopable("a", "A");
                    var b = MakeLoopable("b", "B");
                    var c = MakeRec("c", "NotLoopable");
                    Commit(a, b, c);
                    if (!on)
                    {
                        a.LoopPlayback = true;
                        a.LoopStartUT = 100;
                        a.LoopEndUT = 200;
                        b.LoopPlayback = true;
                    }
                    var desc = new HashSet<int> { 0, 1, 2, 7 };
                    startCapture();
                    run.GroupLoop("G", on, false, Committed, desc, desc, null);
                    break;
                }
                case "GroupArchive_HideAll":
                case "GroupArchive_UnhideAll":
                {
                    bool hide = name == "GroupArchive_HideAll";
                    var a = MakeRec("a", "A");
                    var b = MakeRec("b", "B");
                    var c = MakeRec("c", "C");
                    Commit(a, b, c);
                    if (!hide)
                    {
                        a.Hidden = true;
                        b.Hidden = true;
                        GroupHierarchyStore.AddHiddenGroup("G");
                    }
                    else
                    {
                        b.Hidden = true;
                    }
                    var ui = MakeTable();
                    startCapture();
                    run.GroupArchive(ui, "G", new HashSet<int> { 0, 1 }, Committed, 2, hide);
                    break;
                }
                case "GroupArchive_HideRefused_OneUnfinished":
                case "GroupArchive_HideRefused_TwoUnfinished":
                case "GroupArchive_UnhideWithUnfinished_Allowed":
                {
                    var u1 = MakeUnfinished("u1", "Booster", "bp-1");
                    var u2 = MakeUnfinished("u2", "Probe", "bp-2");
                    var n = MakeRec("n", "Normal");
                    Commit(n, u1, u2);
                    bool two = name == "GroupArchive_HideRefused_TwoUnfinished";
                    bool unhide = name == "GroupArchive_UnhideWithUnfinished_Allowed";
                    if (two || unhide) InstallRewindPoints(u1, u2);
                    else InstallRewindPoints(u1);
                    if (unhide)
                    {
                        n.Hidden = true;
                        u1.Hidden = true;
                        u2.Hidden = true;
                        GroupHierarchyStore.AddHiddenGroup("Munshots");
                    }
                    var ui = MakeTable();
                    output.Append("uf=").Append(EffectiveState.IsUnfinishedFlight(u1))
                        .Append(',').Append(EffectiveState.IsUnfinishedFlight(u2)).Append('\n');
                    startCapture();
                    run.GroupArchive(ui, "Munshots", new HashSet<int> { 0, 1, 2 }, Committed, 3, !unhide);
                    break;
                }
                case "GroupArchive_EmptyFolder_Hide":
                {
                    Commit(MakeRec("a", "A"));
                    var ui = MakeTable();
                    startCapture();
                    run.GroupArchive(ui, "Empty", new HashSet<int>(), Committed, 0, true);
                    break;
                }
                case "RowArchive_Hide":
                case "RowArchive_Unhide":
                {
                    bool hide = name == "RowArchive_Hide";
                    var a = MakeRec("a", "Alpha");
                    a.Hidden = !hide;
                    Commit(a, MakeRec("b", "Beta"));
                    var ui = MakeTable();
                    startCapture();
                    run.RowArchive(ui, a, hide);
                    break;
                }
                case "RowArchive_HideRefused_Unfinished":
                case "RowArchive_UnhideUnfinished_Allowed":
                {
                    bool hide = name == "RowArchive_HideRefused_Unfinished";
                    var u = MakeUnfinished("u1", "Booster", "bp-1");
                    u.Hidden = !hide;
                    Commit(MakeRec("n", "Normal"), u);
                    InstallRewindPoints(u);
                    var ui = MakeTable();
                    output.Append("uf=").Append(EffectiveState.IsUnfinishedFlight(u)).Append('\n');
                    startCapture();
                    run.RowArchive(ui, u, hide);
                    break;
                }
                default:
                    throw new ArgumentException("unknown scenario " + name);
            }
            return output.ToString();
        }

        private static void AppendBlocks(StringBuilder sb, List<GroupDisplayBlock> blocks, List<int> absorbed)
        {
            if (blocks == null)
            {
                sb.Append("blocks=<null>");
            }
            else
            {
                sb.Append("blocks=").Append(blocks.Count);
                foreach (var b in blocks)
                {
                    sb.Append(" [").Append(b.Key ?? "<null>").Append('|').Append(b.DisplayName ?? "<null>")
                        .Append('|').Append(b.Members == null ? "<null>" : string.Join(",", b.Members)).Append(']');
                }
            }
            sb.Append(" absorbed=").Append(absorbed == null ? "<null>" : string.Join(",", absorbed)).Append('\n');
        }

        // ---------------------------------------------------------------------------------
        // Digest
        // ---------------------------------------------------------------------------------

        // A decimal number (the auto loop range prints F1 in the thread culture), rewritten as
        // its double bits so a comma-decimal culture reads the same digest. Integers are left
        // alone: they print culture-free and recording ids carry digits.
        private static readonly Regex NumberRun =
            new Regex(@"-?\d+[.,]\d+(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

        internal static string Bits(double d)
        {
            return BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
        }

        private static string CanonLine(string line)
        {
            return NumberRun.Replace(line, m =>
            {
                double d;
                return double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out d) ? Bits(d) : m.Value;
            });
        }

        internal static string Material(string scenarioOutput, IList<string> logs, IList<string> screens)
        {
            var sb = new StringBuilder();
            sb.Append("out:\n").Append(scenarioOutput);
            sb.Append("recs:\n");
            foreach (var r in RecordingStore.CommittedRecordings)
            {
                sb.Append(r.RecordingId).Append('|').Append(r.Hidden).Append('|').Append(r.LoopPlayback)
                    .Append('|').Append(Bits(r.LoopStartUT)).Append('|').Append(Bits(r.LoopEndUT))
                    .Append('|').Append(r.PlaybackEnabled).Append('\n');
            }
            sb.Append("hiddenGroups:").Append(string.Join(",",
                GroupHierarchyStore.HiddenGroups.OrderBy(g => g, StringComparer.Ordinal))).Append('\n');
            sb.Append("logs:\n");
            foreach (string l in logs) sb.Append(CanonLine(l)).Append('\n');
            sb.Append("screens:\n");
            foreach (string s in screens) sb.Append(s).Append('\n');
            return sb.ToString();
        }

        internal static string Digest(string material, int logCount)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(material))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture) + "/" + logCount.ToString(CultureInfo.InvariantCulture);
        }

        // Runs one scenario on fresh stores and returns its digest and material. Capture starts
        // right before the block's first call: fixture construction logs (a ParsekUI
        // subscribes to static GameEvents, and its line counts what earlier classes left) are
        // not the block's and are not stable across test order.
        internal string Run(string name, IBlocks run, out string material)
        {
            ResetAll();
            string output = RunScenario(name, run, () =>
            {
                logLines.Clear();
                screenMessages.Clear();
            });
            material = Material(output, logLines, screenMessages);
            return Digest(material, logLines.Count);
        }

        internal static readonly Dictionary<string, string> Goldens = new Dictionary<string, string>
        {
            { "Folder_NoDirectMembers", "0885B4DB5AC86E77/0" },
            { "Folder_PlainGroup_NoMissionTree", "1674380F845EF24C/0" },
            { "Folder_MissionTree_AbsorbsRootVessel", "3DE468F7BA54F3F3/1" },
            { "Folder_MissionTree_RootHasOneSegment", "098B39640B067812/0" },
            { "Folder_AbsorbTwice_ThenRelease", "87DF029FE9EA227F/2" },
            { "GroupLoop_RouteBound_Blocked", "20873194E62FB51A/1" },
            { "GroupLoop_Absorbed_On", "5E9B829F67F5547D/3" },
            { "GroupLoop_Absorbed_Off", "F02980F84BF9D628/3" },
            { "GroupLoop_Plain_On", "2F76E67170064DCA/1" },
            { "GroupLoop_Plain_Off", "09D9176597ED29C6/1" },
            { "GroupArchive_HideAll", "0B58A84D5BB65036/4" },
            { "GroupArchive_UnhideAll", "7BE3A37ACBCB9693/2" },
            { "GroupArchive_HideRefused_OneUnfinished", "BF57F6C32E58A65C/2" },
            { "GroupArchive_HideRefused_TwoUnfinished", "0F490E056E797C0B/2" },
            { "GroupArchive_UnhideWithUnfinished_Allowed", "8377E71F085B3E45/2" },
            { "GroupArchive_EmptyFolder_Hide", "57C783C74D555B52/2" },
            { "RowArchive_Hide", "31C5A87E825F401D/3" },
            { "RowArchive_Unhide", "06089304D3530723/3" },
            { "RowArchive_HideRefused_Unfinished", "93EDB278AD83ECF8/1" },
            { "RowArchive_UnhideUnfinished_Allowed", "7FCBC5295955DE71/2" },
        };

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Golden_OriginalBlock(string name)
        {
            string digest = Run(name, new OracleBlocks(), out string material);
            string golden;
            Assert.True(Goldens.TryGetValue(name, out golden),
                "no golden for " + name + ": " + digest + "\n" + material);
            Assert.True(golden == digest, name + ": expected " + golden + " got " + digest + "\n" + material);
        }

        // The sweep reaches every branch of the four blocks: guard against a fixture that
        // stopped producing the shape it names (a classifier change, say), which would leave
        // a golden pinning a different branch than its name.
        [Fact]
        public void Sweep_ReachesEveryBranch()
        {
            Run("GroupArchive_HideRefused_OneUnfinished", new OracleBlocks(), out string m1);
            Assert.Contains("uf=True,False", m1);
            Assert.Contains("1 Unfinished Flight inside", m1);
            Run("GroupArchive_HideRefused_TwoUnfinished", new OracleBlocks(), out string m2);
            Assert.Contains("2 Unfinished Flights inside", m2);
            Run("RowArchive_HideRefused_Unfinished", new OracleBlocks(), out string m3);
            Assert.Contains("uf=True", m3);
            Assert.Contains("Hide refused for Unfinished Flight rec=u1", m3);
            Run("Folder_MissionTree_AbsorbsRootVessel", new OracleBlocks(), out string m4);
            Assert.Contains("absorbed=0,1,4", m4);
            Assert.Contains("is drawn as the folder's own rows", m4);
            Run("Folder_AbsorbTwice_ThenRelease", new OracleBlocks(), out string m5);
            Assert.Contains("is no longer absorbed", m5);
            Run("Folder_NoDirectMembers", new OracleBlocks(), out string m6);
            Assert.Contains("blocks=<null> absorbed=<null>", m6);
            Run("GroupLoop_Absorbed_On", new OracleBlocks(), out string m7);
            Assert.Contains("launched vessel 2 recordings with auto loop range, other 1 without", m7);
            Assert.Contains("Auto loop range: 'Root'", m7);
            Run("GroupLoop_RouteBound_Blocked", new OracleBlocks(), out string m8);
            Assert.Contains("[RouteGuard]", m8);
            Run("GroupArchive_HideAll", new OracleBlocks(), out string m9);
            Assert.Contains("Cache invalidated", m9);
            Assert.Contains("hiddenGroups:G", m9);
        }
    }
}
