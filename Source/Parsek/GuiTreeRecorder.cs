using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Single-frame IMGUI tree recorder. Armed by
    /// <see cref="ArmForNextRepaint(string)"/>, it captures every control the mod draws
    /// during the next Repaint pass - kind, screen rect, text, tooltip, style, enabled
    /// state, nesting - and writes it as JSON next to the screenshots, then disarms
    /// itself. It exists so an agent that cannot see the game can read the real structure
    /// of every Parsek window.
    ///
    /// <para><b>Zero draw-code changes.</b> Nothing in <c>ParsekUI.cs</c> or
    /// <c>UI/*.cs</c> knows this class exists. The whole capture comes from Harmony
    /// interceptions of the UnityEngine IMGUI funnels
    /// (<see cref="Parsek.Patches.GuiTreeRecorderPatches"/>).</para>
    ///
    /// <para><b>Cost when disarmed.</b> Every patch body's first statement is
    /// <c>if (!GuiTreeRecorder.ArmedFlag) return;</c> - one static bool read. That is the
    /// only price the shipping game pays, on every IMGUI control of every frame, so the
    /// flag is a plain static field rather than a property or a settings lookup.</para>
    ///
    /// <para><b>Never throws into OnGUI.</b> An exception escaping a patch body would
    /// abort Unity's GUI pass mid-window and corrupt the layout cache for the rest of the
    /// frame. Every record entry point is wrapped, and the first fault disarms the
    /// recorder and logs once (<see cref="Fault"/>).</para>
    /// </summary>
    internal static class GuiTreeRecorder
    {
        /// <summary>
        /// THE hot flag. Public-ish static field, read by every patch body.
        /// Never set it directly outside this class.
        /// </summary>
        internal static bool ArmedFlag;

        /// <summary>
        /// Cap on recorded events per capture. Parsek's biggest window is a few hundred
        /// controls; 40k is far past any real frame and only exists so a pathological
        /// draw loop cannot exhaust memory inside OnGUI.
        /// </summary>
        internal const int MaxEventsPerCapture = 40000;

        /// <summary>Subdirectory of the KSP root the dump lands in.</summary>
        internal const string OutputDirectoryName = "Screenshots";

        /// <summary>File suffix. The harness harvests <c>Screenshots/</c> by mtime.</summary>
        internal const string OutputSuffix = ".gui.json";

        private static readonly List<GuiTreeEvent> events = new List<GuiTreeEvent>(1024);

        private static string pendingLabel;
        private static string pendingPath;
        private static int captureFrame = -1;
        private static bool capturing;
        private static int recordFaults;
        private static int droppedOverCap;
        private static bool faultLogged;

        /// <summary>Window rects harvested from <c>GUI.DoWindow</c>, keyed by window id.</summary>
        private static readonly Dictionary<int, GuiTreeEvent> pendingWindows =
            new Dictionary<int, GuiTreeEvent>(8);

        /// <summary>
        /// Set by the <c>GUI.DoButton</c> / <c>GUI.DoToggle</c> prefixes and consumed by
        /// the <c>GUI.DoControl</c> prefix. It carries the control KIND, which
        /// <c>DoControl</c> itself cannot know (Button and Toggle reach it through the
        /// same six parameters). Self-healing in both directions - see
        /// <see cref="TakePendingControl"/>.
        /// </summary>
        private static GuiTreeEvent pendingControl;

        /// <summary>Last written dump path, for logging and for the in-game test.</summary>
        internal static string LastWrittenPath { get; private set; }

        /// <summary>Set once a capture has been assembled and written.</summary>
        internal static bool LastCaptureWritten { get; private set; }

        /// <summary>
        /// The assembled tree of the last capture, kept in memory so the live in-game
        /// cell can assert against a checked derivation
        /// (<see cref="GuiTreeGeometry.Inspect"/>) instead of string-matching the JSON it
        /// just wrote. Null until a capture completes.
        /// </summary>
        internal static GuiTreeResult LastTree { get; private set; }

        /// <summary>True while a captured frame is waiting to be flushed by the pump.</summary>
        internal static bool HasPendingFlush
        {
            get { return capturing; }
        }

        /// <summary>
        /// Arms the recorder for the next IMGUI Repaint pass and returns the path the dump
        /// will be written to. Re-arming discards any capture in progress.
        /// </summary>
        internal static string ArmForNextRepaint(string label)
        {
            string safe = SanitizeLabel(label);
            ResetBuffers();
            pendingLabel = safe;
            pendingPath = ResolveOutputPath(safe);
            LastCaptureWritten = false;
            LastWrittenPath = null;
            LastTree = null;
            ArmedFlag = true;
            ParsekLog.Info("GuiTree",
                "armed label=" + safe + " path=" + pendingPath);
            return pendingPath;
        }

        /// <summary>Disarms without writing anything. Safe to call at any time.</summary>
        internal static void Disarm(string reason)
        {
            bool wasArmed = ArmedFlag || capturing;
            ArmedFlag = false;
            capturing = false;
            captureFrame = -1;
            if (wasArmed)
                ParsekLog.Info("GuiTree", "disarmed reason=" + (reason ?? "unspecified"));
        }

        internal static void ResetForTesting()
        {
            ArmedFlag = false;
            ResetBuffers();
            LastWrittenPath = null;
            LastCaptureWritten = false;
            LastTree = null;
            faultLogged = false;
        }

        private static void ResetBuffers()
        {
            events.Clear();
            pendingWindows.Clear();
            pendingControl = null;
            capturing = false;
            captureFrame = -1;
            recordFaults = 0;
            droppedOverCap = 0;
            Array.Clear(GuiTreeFunnels.Hits, 0, GuiTreeFunnels.Count);
        }

        /// <summary>
        /// Called every frame from <see cref="GuiTreeRecorderPump"/>'s LateUpdate, which
        /// runs AFTER the previous frame's OnGUI. Flushes a completed capture. Returns
        /// immediately unless a capture is actually pending.
        /// </summary>
        internal static void PumpPendingFlush()
        {
            if (!capturing)
                return;
            try
            {
                if (Time.frameCount == captureFrame)
                    return;
                FlushCapture();
            }
            catch (Exception ex)
            {
                Fault("pump", ex);
            }
        }

        // ---------------------------------------------------------------- record entry

        /// <summary>
        /// Common gate for every patch body: confirms this is the Repaint pass we want,
        /// opens the capture on first sight, and refuses once the cap is hit.
        /// </summary>
        private static bool Accepting(GuiFunnel funnel)
        {
            if (!ArmedFlag)
                return false;
            Event current = Event.current;
            if (current == null || current.type != EventType.Repaint)
                return false;

            // Counted here rather than at the call site: a hit only means anything for
            // the Repaint pass the dump describes, and counting Layout passes too would
            // make every funnel row read roughly double its node count.
            GuiTreeFunnels.Hits[(int)funnel]++;

            if (!capturing)
            {
                capturing = true;
                captureFrame = Time.frameCount;
            }
            else if (Time.frameCount != captureFrame)
            {
                // A second frame started before the pump flushed the first. Keep the
                // first frame - it is the complete one - and stop recording.
                return false;
            }

            if (events.Count >= MaxEventsPerCapture)
            {
                droppedOverCap++;
                return false;
            }
            return true;
        }

        internal static void RecordLeaf(GuiFunnel funnel, GuiNodeKind kind, Rect rect,
            GUIContent content, GUIStyle style, int? controlId, bool? toggleValue, string textValue)
        {
            try
            {
                if (!Accepting(funnel))
                    return;
                GuiTreeEvent e = NewEvent(GuiTreeOp.Leaf, kind, rect, content, style);
                e.ControlId = controlId;
                e.ToggleValue = toggleValue;
                e.TextValue = textValue;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        internal static void RecordBeginClipContainer(GuiFunnel funnel, GuiNodeKind kind,
            Rect rect, GUIContent content, GUIStyle style)
        {
            try
            {
                if (!Accepting(funnel))
                    return;
                GuiTreeEvent e = NewEvent(GuiTreeOp.Begin, kind, rect, content, style);
                // Measured BEFORE Unity pushes the clip, so the depth direct children
                // will report is one deeper. Normalising here keeps the pure assembler
                // on a single rule (see GuiTreeEvent.ClipDepth).
                if (e.ClipDepth >= 0)
                    e.ClipDepth++;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        internal static void RecordBeginLayoutGroup(GuiFunnel funnel, bool horizontal,
            GUIContent content, GUIStyle style)
        {
            try
            {
                if (!Accepting(funnel))
                    return;
                Rect rect = ReadCurrentLayoutGroupRect();
                GuiTreeEvent e = NewEvent(GuiTreeOp.Begin, GuiNodeKind.LayoutGroup, rect, content, style);
                e.Horizontal = horizontal;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        internal static void RecordEnd(GuiFunnel funnel, GuiNodeKind kind)
        {
            try
            {
                if (!Accepting(funnel))
                    return;
                events.Add(new GuiTreeEvent { Op = GuiTreeOp.End, Kind = kind });
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        /// <summary>
        /// <c>GUI.DoWindow</c> prefix: remembers the window's declared rect, title and
        /// style so <c>CallWindowDelegate</c> can attach them to the window node it
        /// opens. Keyed by window id because the two funnels share nothing else.
        /// </summary>
        internal static void RecordWindowDeclaration(int id, Rect clientRect,
            GUIContent title, GUIStyle style)
        {
            try
            {
                if (!ArmedFlag)
                    return;
                Event current = Event.current;
                if (current == null || current.type != EventType.Repaint)
                    return;
                GuiTreeFunnels.Hits[(int)GuiFunnel.DoWindow]++;
                var e = new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Window,
                    WindowId = id,
                    LocalRect = ToGuiRect(clientRect),
                    // A window's rect is declared in the coordinate space of whatever is
                    // drawing it, which for every Parsek window is the top level, so it is
                    // already screen space. Recorded as both so a nested window (Parsek
                    // draws none today) shows up as a disagreement rather than a lie.
                    Rect = ToGuiRect(clientRect),
                    Text = title != null ? title.text : null,
                    Tooltip = title != null && !string.IsNullOrEmpty(title.tooltip) ? title.tooltip : null,
                    StyleName = style != null ? style.name : null,
                };
                pendingWindows[id] = e;
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.DoWindow), ex);
            }
        }

        /// <summary>
        /// <c>GUI.CallWindowDelegate</c> prefix: opens the window node. This is the funnel
        /// that exactly brackets the window body, and it is a large method Mono will not
        /// inline, so it is the load-bearing one. The declaration from
        /// <see cref="RecordWindowDeclaration"/> is merged in when present; when it is
        /// absent the node still carries an independently measured content origin and the
        /// size Unity handed the callback.
        /// </summary>
        internal static void RecordWindowBegin(int id, float argWidth, float argHeight, GUIStyle style)
        {
            try
            {
                if (!Accepting(GuiFunnel.CallWindowDelegate))
                    return;

                GuiTreeEvent e;
                if (!pendingWindows.TryGetValue(id, out e) || e == null)
                {
                    e = new GuiTreeEvent
                    {
                        Op = GuiTreeOp.Begin,
                        Kind = GuiNodeKind.Window,
                        WindowId = id,
                        StyleName = style != null ? style.name : null,
                    };
                }
                pendingWindows.Remove(id);

                e.ArgWidth = argWidth;
                e.ArgHeight = argHeight;
                Vector2 origin = ScreenPointOfCurrentOrigin();
                e.ContentOriginX = origin.x;
                e.ContentOriginY = origin.y;
                // The native side has already pushed the window's clip by the time the
                // callback runs, so the depth measured here is what children report.
                e.ClipDepth = ReadClipDepth();
                e.Enabled = GUI.enabled;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.CallWindowDelegate), ex);
            }
        }

        /// <summary>
        /// <c>GUI.DoButton</c> / <c>GUI.DoToggle</c> prefix: stages a leaf whose kind only
        /// the caller knows. <c>DoControl</c> claims it; if <c>DoControl</c> never fires
        /// (Mono inlined it) the caller's postfix emits the staged leaf itself, so the
        /// control is never lost whichever side of the pair got inlined.
        /// </summary>
        internal static void StageControl(GuiFunnel funnel, GuiNodeKind kind, Rect rect,
            int controlId, GUIContent content, GUIStyle style, bool? toggleValue)
        {
            try
            {
                if (!Accepting(funnel))
                    return;
                GuiTreeEvent e = NewEvent(GuiTreeOp.Leaf, kind, rect, content, style);
                e.ControlId = controlId;
                e.ToggleValue = toggleValue;
                pendingControl = e;
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        /// <summary>
        /// <c>GUI.DoControl</c> prefix: emits the staged leaf if the caller left one,
        /// otherwise emits a leaf classified from the style name. Either way exactly one
        /// leaf per control.
        /// </summary>
        internal static void RecordControl(Rect rect, int controlId, bool on,
            GUIContent content, GUIStyle style)
        {
            try
            {
                if (!Accepting(GuiFunnel.DoControl))
                    return;
                GuiTreeEvent staged = TakePendingControl(controlId);
                if (staged != null)
                {
                    events.Add(staged);
                    return;
                }
                GuiTreeEvent e = NewEvent(GuiTreeOp.Leaf,
                    GuiTreeAssembler.ClassifyFromStyleName(style != null ? style.name : null, on),
                    rect, content, style);
                e.ControlId = controlId;
                if (on)
                    e.ToggleValue = true;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.DoControl), ex);
            }
        }

        /// <summary>
        /// <c>GUI.DoButton</c> / <c>GUI.DoToggle</c> postfix: emits the staged leaf when
        /// <c>DoControl</c> did not claim it.
        /// </summary>
        internal static void FlushStagedControl(GuiFunnel funnel, int controlId)
        {
            try
            {
                if (!ArmedFlag || !capturing)
                    return;
                GuiTreeEvent staged = TakePendingControl(controlId);
                if (staged != null)
                    events.Add(staged);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        private static GuiTreeEvent TakePendingControl(int controlId)
        {
            GuiTreeEvent staged = pendingControl;
            if (staged == null)
                return null;
            if (staged.ControlId.HasValue && staged.ControlId.Value != controlId)
                return null;
            pendingControl = null;
            return staged;
        }

        // ------------------------------------------------------------------- plumbing

        private static GuiTreeEvent NewEvent(GuiTreeOp op, GuiNodeKind kind, Rect rect,
            GUIContent content, GUIStyle style)
        {
            return new GuiTreeEvent
            {
                Op = op,
                Kind = kind,
                LocalRect = ToGuiRect(rect),
                Rect = ToGuiRect(ToScreenRect(rect)),
                ClipDepth = ReadClipDepth(),
                Text = content != null ? content.text : null,
                Tooltip = content != null && !string.IsNullOrEmpty(content.tooltip) ? content.tooltip : null,
                StyleName = style != null ? style.name : null,
                Enabled = GUI.enabled,
            };
        }

        private static GuiRect ToGuiRect(Rect r)
        {
            return new GuiRect(r.x, r.y, r.width, r.height);
        }

        /// <summary>
        /// Local GUI rect -> screen rect. <c>GUIUtility.GUIToScreenRect</c> is
        /// <c>InternalWindowToScreenPoint(GUIClip.UnclipToWindow(p))</c>: it walks the clip
        /// stack out to the enclosing window and then adds the window's own screen origin,
        /// so it is correct inside a <c>GUI.Window</c> callback, inside groups, and inside
        /// scroll views (where the clip carries the scroll offset). The raw rect is kept
        /// alongside it, so a conversion that ever goes wrong reads as a disagreement in
        /// the dump instead of a silently misplaced box.
        /// </summary>
        private static Rect ToScreenRect(Rect r)
        {
            try
            {
                return GUIUtility.GUIToScreenRect(r);
            }
            catch (Exception)
            {
                return r;
            }
        }

        private static Vector2 ScreenPointOfCurrentOrigin()
        {
            try
            {
                return GUIUtility.GUIToScreenPoint(Vector2.zero);
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
        }

        // GUIClip is internal to UnityEngine.IMGUIModule and Internal_GetCount is an
        // ICall, so it can be INVOKED but never patched. Resolved once, lazily; a failure
        // parks the probe permanently and every event then reports depth -1, which the
        // assembler treats as "unknown" and falls back to explicit Begin/End pairing.
        private static MethodInfo clipCountMethod;
        private static bool clipCountResolved;

        private static int ReadClipDepth()
        {
            try
            {
                if (!clipCountResolved)
                {
                    clipCountResolved = true;
                    Type clip = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip");
                    if (clip != null)
                    {
                        clipCountMethod = clip.GetMethod("Internal_GetCount",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    if (clipCountMethod == null)
                    {
                        ParsekLog.Warn("GuiTree",
                            "clip-depth probe unavailable (UnityEngine.GUIClip.Internal_GetCount not found); "
                            + "tree nesting falls back to Begin/End pairing only");
                    }
                }
                if (clipCountMethod == null)
                    return -1;
                object value = clipCountMethod.Invoke(null, null);
                return value is int ? (int)value : -1;
            }
            catch (Exception)
            {
                clipCountMethod = null;
                return -1;
            }
        }

        // GUILayoutUtility.topLevel is internal and GUILayoutGroup.rect lives on the
        // internal GUILayoutEntry base, so the layout group's rect is only reachable by
        // reflection. During Repaint the group object is the one the Layout pass sized,
        // so its rect is final.
        private static PropertyInfo topLevelProperty;
        private static FieldInfo layoutEntryRectField;
        private static bool layoutRectResolved;

        private static Rect ReadCurrentLayoutGroupRect()
        {
            try
            {
                if (!layoutRectResolved)
                {
                    layoutRectResolved = true;
                    topLevelProperty = typeof(GUILayoutUtility).GetProperty("topLevel",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    Type entry = typeof(GUI).Assembly.GetType("UnityEngine.GUILayoutEntry");
                    if (entry != null)
                    {
                        layoutEntryRectField = entry.GetField("rect",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (topLevelProperty == null || layoutEntryRectField == null)
                    {
                        ParsekLog.Warn("GuiTree",
                            "layout-group rect probe unavailable (topLevel="
                            + (topLevelProperty != null) + " rectField="
                            + (layoutEntryRectField != null)
                            + "); layout groups record a zero rect");
                    }
                }
                if (topLevelProperty == null || layoutEntryRectField == null)
                    return new Rect(0f, 0f, 0f, 0f);
                object group = topLevelProperty.GetValue(null, null);
                if (group == null)
                    return new Rect(0f, 0f, 0f, 0f);
                object value = layoutEntryRectField.GetValue(group);
                return value is Rect ? (Rect)value : new Rect(0f, 0f, 0f, 0f);
            }
            catch (Exception)
            {
                topLevelProperty = null;
                layoutEntryRectField = null;
                return new Rect(0f, 0f, 0f, 0f);
            }
        }

        /// <summary>
        /// Logs once and disarms. An IMGUI pass that already started must never see an
        /// exception from a patch body, so the recorder gives up rather than retrying.
        /// </summary>
        private static void Fault(string funnel, Exception ex)
        {
            recordFaults++;
            ArmedFlag = false;
            if (faultLogged)
                return;
            faultLogged = true;
            try
            {
                ParsekLog.Error("GuiTree",
                    "capture faulted in " + (funnel ?? "unknown") + "; recorder disarmed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception)
            {
                // Nothing left to do: we are inside OnGUI and must not throw.
            }
        }

        // --------------------------------------------------------------------- flush

        private static void FlushCapture()
        {
            ArmedFlag = false;
            capturing = false;

            GuiTreeResult tree = GuiTreeAssembler.Assemble(events);
            var header = new GuiTreeCaptureHeader
            {
                Label = pendingLabel ?? "capture",
                CapturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Frame = captureFrame,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                ScreenshotHint = (pendingLabel ?? "capture") + ".png",
                RecordFaults = recordFaults,
                DroppedOverCap = droppedOverCap,
            };
            for (int i = 0; i < GuiTreeFunnels.Count; i++)
            {
                var funnel = (GuiFunnel)i;
                header.Funnels.Add(new GuiTreeFunnelReport(
                    GuiTreeFunnels.Name(funnel), GuiTreeFunnels.IsPatched(funnel), GuiTreeFunnels.Hits[i]));
            }

            LastTree = tree;
            string json = GuiTreeJson.Write(header, tree);
            string path = pendingPath ?? ResolveOutputPath(header.Label);
            bool written = false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
                written = true;
                LastWrittenPath = path;
                LastCaptureWritten = true;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("GuiTree",
                    "failed to write dump path=" + path + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            ParsekLog.Info("GuiTree",
                "label=" + header.Label
                + " windows=" + tree.WindowCount.ToString(CultureInfo.InvariantCulture)
                + " nodes=" + tree.NodeCount.ToString(CultureInfo.InvariantCulture)
                + " events=" + tree.EventCount.ToString(CultureInfo.InvariantCulture)
                + " strayEnds=" + tree.StrayEnds.ToString(CultureInfo.InvariantCulture)
                + " autoClosed=" + (tree.AutoClosedByClip + tree.AutoClosedByEnd + tree.AutoClosedByRect)
                    .ToString(CultureInfo.InvariantCulture)
                + " unclosed=" + tree.UnclosedAtEnd.ToString(CultureInfo.InvariantCulture)
                + " faults=" + recordFaults.ToString(CultureInfo.InvariantCulture)
                + " written=" + (written ? "1" : "0")
                + " path=" + path);

            events.Clear();
            pendingWindows.Clear();
            pendingControl = null;
        }

        /// <summary>
        /// Label -> filename. Anything that is not a plain filename character becomes an
        /// underscore, so a caller-supplied label can never walk out of the output
        /// directory. Mirrors the intent of <c>RecordingPaths.ValidateRecordingId</c>.
        /// </summary>
        internal static string SanitizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return "guitree";
            var chars = new char[Math.Min(label.Length, 96)];
            for (int i = 0; i < chars.Length; i++)
            {
                char c = label[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                chars[i] = ok ? c : '_';
            }
            string result = new string(chars).Trim('.', '_');
            return string.IsNullOrEmpty(result) ? "guitree" : result;
        }

        private static string ResolveOutputPath(string sanitizedLabel)
        {
            string root;
            try
            {
                root = KSPUtil.ApplicationRootPath;
            }
            catch (Exception)
            {
                root = null;
            }
            if (string.IsNullOrEmpty(root))
                root = Directory.GetCurrentDirectory();
            return Path.Combine(Path.Combine(root, OutputDirectoryName), sanitizedLabel + OutputSuffix);
        }
    }

    /// <summary>
    /// Frame pump for <see cref="GuiTreeRecorder"/>. LateUpdate runs before the frame's
    /// OnGUI and therefore after the PREVIOUS frame's, which is exactly when a completed
    /// capture can be assembled and written without doing file I/O inside an IMGUI pass.
    /// Not a UI surface: no OnGUI, nothing drawn, nothing shown to the player.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class GuiTreeRecorderPump : MonoBehaviour
    {
        void LateUpdate()
        {
            if (!GuiTreeRecorder.HasPendingFlush)
                return;
            GuiTreeRecorder.PumpPendingFlush();
        }
    }
}
