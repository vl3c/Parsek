using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;
using C = Parsek.Tests.RecordingsTableClickHandlerCharacterizationTests;
using G = Parsek.Tests.RecordingsTableGroupTreeCharacterizationTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization goldens for the decision blocks of the Recordings tab's leaf row,
    /// <c>RecordingsTableUI.DrawRecordingRow</c>, that hold no GUI call: the row's
    /// skip-this-row test, the cross-link row tracking, the Enable write, the visual-kind
    /// override of the Status text, the Status hover composition, the Loop write, which
    /// Period cell the row draws, the Watch label and the alignment-debug row capture. The
    /// draw method cannot run headless, so each block is pinned through an ORACLE: the
    /// original block copied verbatim from the source (whole-line comments dropped, private
    /// table state and private statics reached through reflection on the same table, the
    /// method's own `return false` exits turned into a returned skip flag, the one style write
    /// - the visual status style - turned into a returned flag, the Status hover's calls into
    /// the values they returned, and a branch that only draws replaced by the number of the
    /// branch it took). Each sweep runs every branch of its block and prints the block's
    /// outputs and the state it leaves; a golden is that print.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableRecordingRowCharacterizationTests : IDisposable
    {
        internal readonly List<string> logLines = new List<string>();

        public RecordingsTableRecordingRowCharacterizationTests()
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
        // Private statics, reached by name.
        // ---------------------------------------------------------------------------------

        private static MethodInfo Static(string name)
        {
            MethodInfo m = typeof(RecordingsTableUI).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException("RecordingsTableUI", name);
            return m;
        }

        private static bool IsInactiveForDisplay(Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements)
            => (bool)Static("IsInactiveForDisplay").Invoke(null, new object[] { rec, supersedes, retirements });

        private static IReadOnlyList<RecordingSupersedeRelation> CurrentRecordingSupersedesForDisplay()
            => (IReadOnlyList<RecordingSupersedeRelation>)Static("CurrentRecordingSupersedesForDisplay").Invoke(null, null);

        private static IReadOnlyList<RecordingRewindRetirement> CurrentRecordingRewindRetirementsForDisplay()
            => (IReadOnlyList<RecordingRewindRetirement>)Static("CurrentRecordingRewindRetirementsForDisplay").Invoke(null, null);

        private static string CombineTooltipText(string primary, string secondary)
            => (string)Static("CombineTooltipText").Invoke(null, new object[] { primary, secondary });

        // ---------------------------------------------------------------------------------
        // The oracles: the ORIGINAL blocks of DrawRecordingRow, verbatim.
        // ---------------------------------------------------------------------------------

        // The row's two early exits; true where the original returned false before drawing.
        internal static bool OracleSkipRow(Recording rec, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (IsInactiveForDisplay(
                    rec,
                    supersedes ?? CurrentRecordingSupersedesForDisplay(),
                    CurrentRecordingRewindRetirementsForDisplay())) return true;
            if (rec.Hidden && GroupHierarchyStore.HideActive) return true;
            return false;
        }

        internal static void OracleCrossLink(RecordingsTableUI ui, Recording rec)
        {
            if (!string.IsNullOrEmpty(G.Get<string>(ui, "pendingScrollToRecordingId")) &&
                rec.RecordingId == G.Get<string>(ui, "pendingScrollToRecordingId"))
            {
                G.Set(ui, "pendingScrollRowIndex", G.Get<int>(ui, "renderedRowCounter"));
                ParsekLog.Verbose("UI",
                    $"Cross-link: found \"{rec.VesselName}\" at rendered row {G.Get<int>(ui, "renderedRowCounter")}");
                G.Set(ui, "pendingScrollToRecordingId", null);
            }
            G.Set(ui, "renderedRowCounter", G.Get<int>(ui, "renderedRowCounter") + 1);
        }

        internal static void OracleApplyEnabled(Recording rec, bool enabled)
        {
            RecordingStore.SetRecordingPlaybackEnabled(rec, enabled);
            ParsekLog.Info("UI", $"Recording '{rec.VesselName}' playback {(enabled ? "enabled" : "disabled")}" +
                (!string.IsNullOrEmpty(rec.SegmentPhase) ? $" (segment: {RecordingSegmentLabels.GetSegmentPhaseLabel(rec)})" : ""));
        }

        // The visual-kind override; true where the original replaced the Status style.
        internal static bool OracleVisualStatus(RecordingVisualKind visualKind, ref string statusText,
            bool statusTextIsTerminalState)
        {
            bool restyled = false;
            string visualStatusText = RecordingsTableUI.FormatRecordingVisualStatusText(
                visualKind, statusText, statusTextIsTerminalState);
            if (visualStatusText != statusText)
            {
                statusText = visualStatusText;
                if (!statusTextIsTerminalState)
                    restyled = true;
            }
            return restyled;
        }

        internal static string OracleStatusTooltip(string statusPlaceTooltip, string statusStatsTooltip,
            string manifestSummary, string visualStatusTooltip, string chainStatusTooltip,
            string dockPartnerTooltip)
        {
            string statusTooltip = CombineTooltipText(
                CombineTooltipText(
                    CombineTooltipText(statusPlaceTooltip, statusStatsTooltip),
                    manifestSummary),
                CombineTooltipText(
                    CombineTooltipText(
                        visualStatusTooltip,
                        chainStatusTooltip),
                    dockPartnerTooltip));
            if (string.IsNullOrEmpty(statusTooltip))
                statusTooltip = "How this flight is doing: counting down to launch, flying now, or how it ended.";
            return statusTooltip;
        }

        internal static void OracleLoopToggle(RecordingsTableUI ui, Recording rec, int ri, bool loop,
            bool rowRouteBound)
        {
            if (rowRouteBound)
            {
                ParsekLog.Info("RouteGuard",
                    $"Recordings-tab per-recording Loop blocked for '{rec.VesselName}' " +
                    $"(tree={rec.TreeId} bound by route); request={loop} ignored");
            }
            else
            {
                rec.LoopPlayback = loop;
                RecordingsTableUI.ApplyAutoLoopRange(rec, loop);
                if (!loop && G.Get<int>(ui, "loopPeriodFocusedRi") == ri)
                    G.Set(ui, "loopPeriodFocusedRi", -1);
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' loop playback set to {loop}");
            }
        }

        // Which Period cell the row draws: 0 blank, 1 blank with a hover, 2 the value.
        internal static int OraclePeriodCell(bool suppressRowLoop, bool loopPlayback)
        {
            if (suppressRowLoop)
            {
                return 0;
            }
            else if (!loopPlayback)
            {
                return 1;
            }
            else
            {
                return 2;
            }
        }

        internal static string OracleWatchLabel(bool isWatching)
        {
            string watchLabel = isWatching ? "W*" : "W";
            return watchLabel;
        }

        internal static void OracleAlignRowCapture(RecordingsTableUI ui)
        {
            G.Set(ui, "alignmentDebugRowCaptured", true);
            ParsekLog.Info("UI", "AlignDbg ROW: " + G.Get<StringBuilder>(ui, "alignmentDebugRowLog").ToString());
            if (G.Get<bool>(ui, "alignmentDebugHeaderCaptured")) G.Set(ui, "alignmentDebugArmed", false);
        }

        // ---------------------------------------------------------------------------------
        // The units, run through the oracle here and through the shipped helpers in
        // RecordingsTableRecordingRowHelperTests.
        // ---------------------------------------------------------------------------------

        internal interface IUnits
        {
            bool SkipRow(Recording rec, IReadOnlyList<RecordingSupersedeRelation> supersedes);
            void CrossLink(RecordingsTableUI ui, Recording rec);
            void ApplyEnabled(Recording rec, bool enabled);
            bool VisualStatus(RecordingVisualKind visualKind, ref string statusText, bool statusTextIsTerminalState);
            string StatusTooltip(string place, string stats, string manifest, string visual, string chain, string dock);
            void LoopToggle(RecordingsTableUI ui, Recording rec, int ri, bool loop, bool rowRouteBound);
            int PeriodCell(bool suppressRowLoop, bool loopPlayback);
            string WatchLabel(bool isWatching);
            void AlignRowCapture(RecordingsTableUI ui);
        }

        internal sealed class OracleUnits : IUnits
        {
            public bool SkipRow(Recording rec, IReadOnlyList<RecordingSupersedeRelation> s) => OracleSkipRow(rec, s);
            public void CrossLink(RecordingsTableUI ui, Recording rec) => OracleCrossLink(ui, rec);
            public void ApplyEnabled(Recording rec, bool e) => OracleApplyEnabled(rec, e);
            public bool VisualStatus(RecordingVisualKind k, ref string t, bool term) => OracleVisualStatus(k, ref t, term);
            public string StatusTooltip(string p, string s, string m, string v, string c, string d)
                => OracleStatusTooltip(p, s, m, v, c, d);
            public void LoopToggle(RecordingsTableUI ui, Recording rec, int ri, bool loop, bool bound)
                => OracleLoopToggle(ui, rec, ri, loop, bound);
            public int PeriodCell(bool s, bool l) => OraclePeriodCell(s, l);
            public string WatchLabel(bool w) => OracleWatchLabel(w);
            public void AlignRowCapture(RecordingsTableUI ui) => OracleAlignRowCapture(ui);
        }

        // ---------------------------------------------------------------------------------
        // Sweeps. Each builds its own fixtures on fresh stores, runs every branch of one
        // block and prints what the block returned and the state it left behind.
        // ---------------------------------------------------------------------------------

        internal static readonly string[] SweepNames =
        {
            "SkipRow", "CrossLink", "ApplyEnabled", "VisualStatus", "StatusTooltip",
            "LoopToggle", "PeriodCell", "WatchLabel", "AlignRowCapture",
        };

        public static IEnumerable<object[]> Sweeps()
        {
            foreach (string n in SweepNames) yield return new object[] { n };
        }

        private static string B(bool b) => b ? "T" : "F";

        private static string S(string s) => s == null ? "<null>" : "\"" + s + "\"";

        private static string D(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        private static void Logs(StringBuilder sb, List<string> logs)
        {
            foreach (string l in logs) sb.Append("  log ").Append(l).Append('\n');
        }

        private static string CrossLinkState(RecordingsTableUI ui)
        {
            return "pending=" + S(G.Get<string>(ui, "pendingScrollToRecordingId"))
                + " rowIndex=" + G.Get<int>(ui, "pendingScrollRowIndex").ToString(CultureInfo.InvariantCulture)
                + " counter=" + G.Get<int>(ui, "renderedRowCounter").ToString(CultureInfo.InvariantCulture);
        }

        private static string LoopState(RecordingsTableUI ui, Recording rec)
        {
            return "loop=" + B(rec.LoopPlayback) + " range=" + D(rec.LoopStartUT) + ".." + D(rec.LoopEndUT)
                + " focused=" + G.Get<int>(ui, "loopPeriodFocusedRi").ToString(CultureInfo.InvariantCulture);
        }

        private static string AlignState(RecordingsTableUI ui)
        {
            return "armed=" + B(G.Get<bool>(ui, "alignmentDebugArmed"))
                + " header=" + B(G.Get<bool>(ui, "alignmentDebugHeaderCaptured"))
                + " row=" + B(G.Get<bool>(ui, "alignmentDebugRowCaptured"));
        }

        private static ParsekScenario InstallScenario(List<RecordingSupersedeRelation> supersedes,
            List<RecordingRewindRetirement> retirements)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = supersedes,
                RecordingRewindRetirements = retirements,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        internal static string RunSweep(string name, IUnits u, List<string> logs)
        {
            var sb = new StringBuilder();
            switch (name)
            {
                case "SkipRow":
                {
                    var a = C.MakeRec("a", "A", 0, 10);
                    var b = C.MakeRec("b", "B", 10, 20);
                    var c = C.MakeRec("c", "C", 20, 30);
                    var n = C.MakeRec("n", "N", 30, 40);
                    C.Commit(a, b, c, n);
                    logs.Clear();
                    var supersedeA = new List<RecordingSupersedeRelation>
                    {
                        new RecordingSupersedeRelation { RelationId = "rel-a", OldRecordingId = "a", NewRecordingId = "n" },
                    };
                    var none = new List<RecordingSupersedeRelation>();
                    sb.Append("noScenario plain=").Append(B(u.SkipRow(a, null))).Append('\n');
                    sb.Append("noScenario explicitSupersede=").Append(B(u.SkipRow(a, supersedeA))).Append('\n');
                    sb.Append("noScenario explicitOther=").Append(B(u.SkipRow(b, supersedeA))).Append('\n');
                    InstallScenario(supersedeA, new List<RecordingRewindRetirement>
                    {
                        new RecordingRewindRetirement { RetirementId = "ret-c", RecordingId = "c" },
                    });
                    sb.Append("scenario nullList superseded=").Append(B(u.SkipRow(a, null))).Append('\n');
                    sb.Append("scenario emptyList superseded=").Append(B(u.SkipRow(a, none))).Append('\n');
                    sb.Append("scenario retired=").Append(B(u.SkipRow(c, none))).Append('\n');
                    sb.Append("scenario plain=").Append(B(u.SkipRow(b, null))).Append('\n');
                    b.Hidden = true;
                    GroupHierarchyStore.HideActive = true;
                    sb.Append("hidden hideActive=").Append(B(u.SkipRow(b, null))).Append('\n');
                    GroupHierarchyStore.HideActive = false;
                    sb.Append("hidden hideOff=").Append(B(u.SkipRow(b, null))).Append('\n');
                    b.Hidden = false;
                    GroupHierarchyStore.HideActive = true;
                    sb.Append("shown hideActive=").Append(B(u.SkipRow(b, null))).Append('\n');
                    Logs(sb, logs);
                    break;
                }
                case "CrossLink":
                {
                    var a = C.MakeRec("a", "Alpha", 0, 10);
                    var b = C.MakeRec("b", "Beta", 10, 20);
                    var e = C.MakeRec("", "NoId", 20, 30);
                    var ui = C.MakeTable();
                    logs.Clear();
                    sb.Append("start ").Append(CrossLinkState(ui)).Append('\n');
                    u.CrossLink(ui, a);
                    sb.Append("noPending a ").Append(CrossLinkState(ui)).Append('\n');
                    G.Set(ui, "pendingScrollToRecordingId", "b");
                    u.CrossLink(ui, a);
                    sb.Append("pending=b a ").Append(CrossLinkState(ui)).Append('\n');
                    u.CrossLink(ui, b);
                    sb.Append("pending=b b ").Append(CrossLinkState(ui)).Append('\n');
                    u.CrossLink(ui, b);
                    sb.Append("cleared b ").Append(CrossLinkState(ui)).Append('\n');
                    G.Set(ui, "pendingScrollToRecordingId", "");
                    u.CrossLink(ui, e);
                    sb.Append("pending=empty noId ").Append(CrossLinkState(ui)).Append('\n');
                    Logs(sb, logs);
                    break;
                }
                case "ApplyEnabled":
                {
                    var a = C.MakeRec("a", "Alpha", 0, 10);
                    var b = C.MakeRec("b", "Beta", 10, 20);
                    b.SegmentPhase = "atmo";
                    C.Commit(a, b);
                    logs.Clear();
                    u.ApplyEnabled(a, false);
                    sb.Append("a off: ").Append(B(a.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.ApplyEnabled(b, false);
                    sb.Append("b off: ").Append(B(b.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.ApplyEnabled(b, true);
                    sb.Append("b on: ").Append(B(b.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.ApplyEnabled(a, false);
                    sb.Append("a off again: ").Append(B(a.PlaybackEnabled)).Append('\n');
                    Logs(sb, logs);
                    break;
                }
                case "VisualStatus":
                {
                    foreach (RecordingVisualKind kind in Enum.GetValues(typeof(RecordingVisualKind)))
                        foreach (string text in new[] { "Landed", "", null })
                            foreach (bool term in new[] { false, true })
                            {
                                string t = text;
                                bool restyled = u.VisualStatus(kind, ref t, term);
                                sb.Append(kind).Append('/').Append(S(text)).Append('/').Append(B(term))
                                    .Append(" -> ").Append(B(restyled)).Append(' ').Append(S(t)).Append('\n');
                            }
                    break;
                }
                case "StatusTooltip":
                {
                    var cases = new[]
                    {
                        new string[] { null, null, null, null, "", null },
                        new string[] { "", "", "", "", "", "" },
                        new string[] { "Ends: Kerbin", null, null, null, "", null },
                        new string[] { null, "Max alt 70 km", null, null, "", null },
                        new string[] { "Ends: Kerbin", "Max alt 70 km", null, null, "", null },
                        new string[] { null, null, "Used: 10 LF", null, "", null },
                        new string[] { null, null, null, "Static placeholder", "", null },
                        new string[] { null, null, null, null, "Chain: 2 ghosts", null },
                        new string[] { null, null, null, null, "", "Docked Station" },
                        new string[] { null, null, null, "Static placeholder", "Chain: 2 ghosts", "Docked Station" },
                        new string[] { "Ends: Kerbin", "Max alt 70 km", "Used: 10 LF", "Static placeholder", "Chain: 2 ghosts", "Docked Station" },
                        new string[] { "Ends: Kerbin", null, "Used: 10 LF", null, "", "Docked Station" },
                    };
                    foreach (string[] k in cases)
                    {
                        sb.Append(string.Join("|", k.Select(S))).Append(" -> ")
                            .Append(S(u.StatusTooltip(k[0], k[1], k[2], k[3], k[4], k[5]))).Append('\n');
                    }
                    break;
                }
                case "LoopToggle":
                {
                    var a = C.MakeRec("a", "Alpha", 0, 10);
                    a.TreeId = "tree-1";
                    a.LoopStartUT = 2;
                    a.LoopEndUT = 8;
                    C.Commit(a);
                    var ui = C.MakeTable();
                    G.Set(ui, "loopPeriodFocusedRi", 3);
                    logs.Clear();
                    u.LoopToggle(ui, a, 3, true, true);
                    sb.Append("bound on ").Append(LoopState(ui, a)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.LoopToggle(ui, a, 3, true, false);
                    sb.Append("free on ").Append(LoopState(ui, a)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.LoopToggle(ui, a, 4, false, false);
                    sb.Append("free off otherRow ").Append(LoopState(ui, a)).Append('\n');
                    Logs(sb, logs);
                    a.LoopPlayback = true;
                    a.LoopStartUT = 2;
                    a.LoopEndUT = 8;
                    logs.Clear();
                    u.LoopToggle(ui, a, 3, false, false);
                    sb.Append("free off focusedRow ").Append(LoopState(ui, a)).Append('\n');
                    Logs(sb, logs);
                    logs.Clear();
                    u.LoopToggle(ui, a, 3, false, true);
                    sb.Append("bound off ").Append(LoopState(ui, a)).Append('\n');
                    Logs(sb, logs);
                    break;
                }
                case "PeriodCell":
                    foreach (bool s in new[] { false, true })
                        foreach (bool l in new[] { false, true })
                            sb.Append("suppress=").Append(B(s)).Append(" loop=").Append(B(l))
                                .Append(" -> ").Append(u.PeriodCell(s, l).ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case "WatchLabel":
                    sb.Append(u.WatchLabel(true)).Append('|').Append(u.WatchLabel(false));
                    break;
                case "AlignRowCapture":
                {
                    var ui = C.MakeTable();
                    ui.ArmAlignmentDebug();
                    G.Get<StringBuilder>(ui, "alignmentDebugRowLog").Append("rowToggle[x=1] ");
                    logs.Clear();
                    u.AlignRowCapture(ui);
                    sb.Append("headerPending ").Append(AlignState(ui)).Append('\n');
                    Logs(sb, logs);
                    ui.ArmAlignmentDebug();
                    G.Set(ui, "alignmentDebugHeaderCaptured", true);
                    G.Get<StringBuilder>(ui, "alignmentDebugRowLog").Append("rowIdx[x=2] ");
                    logs.Clear();
                    u.AlignRowCapture(ui);
                    sb.Append("headerDone ").Append(AlignState(ui)).Append('\n');
                    Logs(sb, logs);
                    break;
                }
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
                "SkipRow",
                "noScenario plain=F\n"
                + "noScenario explicitSupersede=T\n"
                + "noScenario explicitOther=F\n"
                + "scenario nullList superseded=T\n"
                + "scenario emptyList superseded=F\n"
                + "scenario retired=T\n"
                + "scenario plain=F\n"
                + "hidden hideActive=T\n"
                + "hidden hideOff=F\n"
                + "shown hideActive=F\n"
            },
            {
                "CrossLink",
                "start pending=<null> rowIndex=-1 counter=0\n"
                + "noPending a pending=<null> rowIndex=-1 counter=1\n"
                + "pending=b a pending=\"b\" rowIndex=-1 counter=2\n"
                + "pending=b b pending=<null> rowIndex=2 counter=3\n"
                + "cleared b pending=<null> rowIndex=2 counter=4\n"
                + "pending=empty noId pending=\"\" rowIndex=2 counter=5\n"
                + "  log [Parsek][VERBOSE][UI] Cross-link: found \"Beta\" at rendered row 2\n"
            },
            {
                "ApplyEnabled",
                "a off: F\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback disabled: rec=a vessel=\"Alpha\"\n"
                + "  log [Parsek][INFO][UI] Recording 'Alpha' playback disabled\n"
                + "b off: F\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback disabled: rec=b vessel=\"Beta\"\n"
                + "  log [Parsek][INFO][UI] Recording 'Beta' playback disabled (segment: atmo)\n"
                + "b on: T\n"
                + "  log [Parsek][INFO][RecordingStore] Recording playback enabled: rec=b vessel=\"Beta\"\n"
                + "  log [Parsek][INFO][UI] Recording 'Beta' playback enabled (segment: atmo)\n"
                + "a off again: F\n"
                + "  log [Parsek][INFO][UI] Recording 'Alpha' playback disabled\n"
            },
            {
                "VisualStatus",
                "Normal/\"Landed\"/F -> F \"Landed\"\n"
                + "Normal/\"Landed\"/T -> F \"Landed\"\n"
                + "Normal/\"\"/F -> F \"\"\n"
                + "Normal/\"\"/T -> F \"\"\n"
                + "Normal/<null>/F -> F <null>\n"
                + "Normal/<null>/T -> F <null>\n"
                + "StaticPlaceholder/\"Landed\"/F -> T \"static\"\n"
                + "StaticPlaceholder/\"Landed\"/T -> F \"Landed still\"\n"
                + "StaticPlaceholder/\"\"/F -> T \"static\"\n"
                + "StaticPlaceholder/\"\"/T -> F \"static\"\n"
                + "StaticPlaceholder/<null>/F -> T \"static\"\n"
                + "StaticPlaceholder/<null>/T -> F \"static\"\n"
                + "StationaryTail/\"Landed\"/F -> T \"stationary\"\n"
                + "StationaryTail/\"Landed\"/T -> F \"Landed still\"\n"
                + "StationaryTail/\"\"/F -> T \"stationary\"\n"
                + "StationaryTail/\"\"/T -> F \"stationary\"\n"
                + "StationaryTail/<null>/F -> T \"stationary\"\n"
                + "StationaryTail/<null>/T -> F \"stationary\"\n"
            },
            {
                "StatusTooltip",
                "<null>|<null>|<null>|<null>|\"\"|<null> -> \"How this flight is doing: counting down to launch, flying now, or how it ended.\"\n"
                + "\"\"|\"\"|\"\"|\"\"|\"\"|\"\" -> \"How this flight is doing: counting down to launch, flying now, or how it ended.\"\n"
                + "\"Ends: Kerbin\"|<null>|<null>|<null>|\"\"|<null> -> \"Ends: Kerbin\"\n"
                + "<null>|\"Max alt 70 km\"|<null>|<null>|\"\"|<null> -> \"Max alt 70 km\"\n"
                + "\"Ends: Kerbin\"|\"Max alt 70 km\"|<null>|<null>|\"\"|<null> -> \"Ends: Kerbin - Max alt 70 km\"\n"
                + "<null>|<null>|\"Used: 10 LF\"|<null>|\"\"|<null> -> \"Used: 10 LF\"\n"
                + "<null>|<null>|<null>|\"Static placeholder\"|\"\"|<null> -> \"Static placeholder\"\n"
                + "<null>|<null>|<null>|<null>|\"Chain: 2 ghosts\"|<null> -> \"Chain: 2 ghosts\"\n"
                + "<null>|<null>|<null>|<null>|\"\"|\"Docked Station\" -> \"Docked Station\"\n"
                + "<null>|<null>|<null>|\"Static placeholder\"|\"Chain: 2 ghosts\"|\"Docked Station\" -> \"Static placeholder - Chain: 2 ghosts - Docked Station\"\n"
                + "\"Ends: Kerbin\"|\"Max alt 70 km\"|\"Used: 10 LF\"|\"Static placeholder\"|\"Chain: 2 ghosts\"|\"Docked Station\" -> \"Ends: Kerbin - Max alt 70 km - Used: 10 LF - Static placeholder - Chain: 2 ghosts - Docked Station\"\n"
                + "\"Ends: Kerbin\"|<null>|\"Used: 10 LF\"|<null>|\"\"|\"Docked Station\" -> \"Ends: Kerbin - Used: 10 LF - Docked Station\"\n"
            },
            {
                "LoopToggle",
                "bound on loop=F range=2..8 focused=3\n"
                + "  log [Parsek][INFO][RouteGuard] Recordings-tab per-recording Loop blocked for 'Alpha' (tree=tree-1 bound by route); request=True ignored\n"
                + "free on loop=T range=2..8 focused=3\n"
                + "  log [Parsek][INFO][UI] Recording 'Alpha' loop playback set to True\n"
                + "free off otherRow loop=F range=NaN..NaN focused=3\n"
                + "  log [Parsek][INFO][UI] Loop range cleared for 'Alpha'\n"
                + "  log [Parsek][INFO][UI] Recording 'Alpha' loop playback set to False\n"
                + "free off focusedRow loop=F range=NaN..NaN focused=-1\n"
                + "  log [Parsek][INFO][UI] Loop range cleared for 'Alpha'\n"
                + "  log [Parsek][INFO][UI] Recording 'Alpha' loop playback set to False\n"
                + "bound off loop=F range=NaN..NaN focused=-1\n"
                + "  log [Parsek][INFO][RouteGuard] Recordings-tab per-recording Loop blocked for 'Alpha' (tree=tree-1 bound by route); request=False ignored\n"
            },
            {
                "PeriodCell",
                "suppress=F loop=F -> 1\n"
                + "suppress=F loop=T -> 2\n"
                + "suppress=T loop=F -> 0\n"
                + "suppress=T loop=T -> 0\n"
            },
            {
                "WatchLabel",
                "W*|W"
            },
            {
                "AlignRowCapture",
                "headerPending armed=T header=F row=T\n"
                + "  log [Parsek][INFO][UI] AlignDbg ROW: rowToggle[x=1] \n"
                + "headerDone armed=F header=T row=T\n"
                + "  log [Parsek][INFO][UI] AlignDbg ROW: rowIdx[x=2] \n"
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
