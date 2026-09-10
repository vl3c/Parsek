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
    /// <see cref="ArmForNextRepaint(string)"/>, it captures every control the process
    /// draws during the next Repaint pass - kind, screen rect, text, tooltip, style,
    /// enabled state, nesting - and writes it as JSON next to the screenshots, then
    /// disarms itself. It exists so an agent that cannot see the game can read the real
    /// structure of every Parsek window.
    ///
    /// <para><b>Zero draw-code changes.</b> Nothing in <c>ParsekUI.cs</c> or
    /// <c>UI/*.cs</c> knows this class exists. The whole capture comes from Harmony
    /// interceptions of the UnityEngine IMGUI funnels
    /// (<see cref="Parsek.Patches.GuiTreeRecorderPatches"/>).</para>
    ///
    /// <para><b>Cost when disarmed: NOTHING.</b> The interceptions are installed by
    /// <c>GuiTreeRecorderPatches.Apply()</c> at arm time and removed again when the
    /// capture flushes, so a disarmed recorder holds no Harmony detour on any IMGUI
    /// method and no IMGUI consumer in the process (KSP's debug UI, MechJeb, KER,
    /// ClickThroughBlocker) pays anything at all. The <see cref="ArmedFlag"/> check at the
    /// top of every patch body still matters, because the patches can outlive the capture
    /// by one frame: the unpatch is DEFERRED out of the GUI pass rather than done from
    /// inside a method Harmony is currently detouring.</para>
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

        /// <summary>
        /// File suffix. <c>hlib.ARTIFACT_SHOTS_SUFFIXES</c> carries the same string: the
        /// harness harvests <c>Screenshots/</c> by mtime, and this suffix is what makes a
        /// dump ride along with a run's images into <c>results/&lt;runId&gt;_shots/</c>.
        /// </summary>
        internal const string OutputSuffix = ".gui.json";

        /// <summary>
        /// The layout-group type <c>GUILayoutUtility.BeginLayoutGroup</c> is called with
        /// for a real <c>BeginHorizontal</c> / <c>BeginVertical</c>. Anything else - today
        /// only <c>UnityEngine.GUIScrollGroup</c>, from <c>GUILayout.BeginScrollView</c> -
        /// is a carrier for a container that already has a node of its own, and recording
        /// it would duplicate the container and put its End in the wrong place.
        /// </summary>
        private const string PlainLayoutGroupTypeName = "UnityEngine.GUILayoutGroup";

        private static readonly List<GuiTreeEvent> events = new List<GuiTreeEvent>(1024);

        private static string pendingLabel;
        private static string pendingPath;
        private static int captureFrame = -1;
        private static bool capturing;
        private static int recordFaults;
        private static int droppedOverCap;
        private static bool faultLogged;
        private static bool unpatchPending;

        // Read ONCE when the capture opens, not at flush time (which is the NEXT frame,
        // by which point a resolution change or a matrix reset would describe a different
        // screen than the one the rects were measured in).
        private static int captureScreenWidth;
        private static int captureScreenHeight;
        private static bool matrixIsIdentity = true;
        private static float matrixM00 = 1f;
        private static float matrixM11 = 1f;
        private static float matrixM03;
        private static float matrixM13;

        /// <summary>Window rects harvested from <c>GUI.DoWindow</c>, keyed by window id.</summary>
        private static readonly Dictionary<int, GuiTreeEvent> pendingWindows =
            new Dictionary<int, GuiTreeEvent>(8);

        /// <summary>
        /// One entry per live <c>GUILayoutUtility.BeginLayoutGroup</c>: the group's
        /// orientation when it was recorded, or null when it was a carrier we skipped
        /// (a scroll view's <c>GUIScrollGroup</c>). <c>EndLayoutGroup</c> pops it, which
        /// is the only way an argument-less End can know WHICH group it closes.
        /// </summary>
        private static readonly List<bool?> layoutGroupStack = new List<bool?>(32);

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

        /// <summary>Patch-body exceptions swallowed during the last captured frame.</summary>
        internal static int LastRecordFaults { get; private set; }

        /// <summary>Events the last capture dropped at the per-frame cap.</summary>
        internal static int LastDroppedOverCap { get; private set; }

        /// <summary>True when the last capture ran under a non-identity <c>GUI.matrix</c>.</summary>
        internal static bool LastMatrixNonIdentity { get; private set; }

        /// <summary>True while a captured frame is waiting to be flushed by the pump.</summary>
        internal static bool HasPendingFlush
        {
            get { return capturing; }
        }

        /// <summary>
        /// True while the LateUpdate pump has something to do: a capture to flush, or a
        /// deferred unpatch to perform.
        /// </summary>
        internal static bool HasPendingWork
        {
            get { return capturing || unpatchPending; }
        }

        /// <summary>
        /// Why an arm request must be refused, or null when it may proceed. Pure, so the
        /// decision is testable without a Unity GUI pass.
        ///
        /// <para>Arming from INSIDE an IMGUI pass is refused for two reasons: arming
        /// installs Harmony patches on the very methods the current call stack is running,
        /// and the pass already under way is half-drawn, so the capture would start
        /// mid-window and record a truncated tree. Callers arm from Update, a coroutine or
        /// the command seam - never from OnGUI.</para>
        /// </summary>
        internal static string ClassifyArmRefusal(bool insideGuiPass)
        {
            return insideGuiPass ? "inside-gui-pass" : null;
        }

        /// <summary>
        /// Arms the recorder for the next IMGUI Repaint pass and returns the path the dump
        /// will be written to, or null when the request was refused. Re-arming discards
        /// any capture in progress.
        /// </summary>
        internal static string ArmForNextRepaint(string label)
        {
            string refusal = ClassifyArmRefusal(IsInsideGuiPass());
            if (refusal != null)
            {
                ParsekLog.Warn("GuiTree", "arm refused reason=" + refusal
                    + "; arm from Update or a coroutine, never from inside OnGUI");
                return null;
            }

            string safe = SanitizeLabel(label);
            ResetBuffers();
            faultLogged = false;
            pendingLabel = safe;
            pendingPath = ResolveOutputPath(safe);
            LastCaptureWritten = false;
            LastWrittenPath = null;
            LastTree = null;

            Patches.GuiTreeRecorderPatches.Apply();
            unpatchPending = false;
            int patched = 0;
            for (int i = 0; i < GuiTreeFunnels.Count; i++)
            {
                GuiTreeFunnels.PatchedAtArm[i] = GuiTreeFunnels.IsPatched((GuiFunnel)i);
                if (GuiTreeFunnels.PatchedAtArm[i])
                    patched++;
            }

            ArmedFlag = true;
            ParsekLog.Info("GuiTree", "armed label=" + safe
                + " patchedFunnels=" + patched.ToString(CultureInfo.InvariantCulture)
                + "/" + GuiTreeFunnels.Count.ToString(CultureInfo.InvariantCulture)
                + " path=" + pendingPath);
            return pendingPath;
        }

        /// <summary>Disarms without writing anything. Safe to call at any time.</summary>
        internal static void Disarm(string reason)
        {
            bool wasArmed = ArmedFlag || capturing;
            ArmedFlag = false;
            capturing = false;
            captureFrame = -1;
            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            pendingControl = null;
            RequestUnpatch();
            if (wasArmed)
                ParsekLog.Info("GuiTree", "disarmed reason=" + (reason ?? "unspecified"));
        }

        internal static void ResetForTesting()
        {
            ArmedFlag = false;
            ResetBuffers();
            pendingLabel = null;
            pendingPath = null;
            LastWrittenPath = null;
            LastCaptureWritten = false;
            LastTree = null;
            LastRecordFaults = 0;
            LastDroppedOverCap = 0;
            LastMatrixNonIdentity = false;
            faultLogged = false;
            unpatchPending = false;
        }

        private static void ResetBuffers()
        {
            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            pendingControl = null;
            capturing = false;
            captureFrame = -1;
            recordFaults = 0;
            droppedOverCap = 0;
            captureScreenWidth = 0;
            captureScreenHeight = 0;
            matrixIsIdentity = true;
            matrixM00 = 1f;
            matrixM11 = 1f;
            matrixM03 = 0f;
            matrixM13 = 0f;
            Array.Clear(GuiTreeFunnels.Hits, 0, GuiTreeFunnels.Count);
            // Re-resolve the reflection probes on every arm rather than parking them for
            // the process lifetime: a probe that failed once - a transient during scene
            // load, a type not yet loaded - must not silently cost every later capture
            // its clip depths or its layout-group rects.
            clipCountResolved = false;
            clipCount = null;
            layoutProbeResolved = false;
            layoutEntryRectField = null;
            layoutGroupIsVerticalField = null;
        }

        /// <summary>
        /// Called every frame from <see cref="GuiTreeRecorderPump"/>'s LateUpdate, which
        /// runs AFTER the previous frame's OnGUI. Flushes a completed capture and performs
        /// a deferred unpatch. Returns immediately unless there is work.
        /// </summary>
        internal static void PumpPendingFlush()
        {
            if (capturing)
            {
                try
                {
                    if (Time.frameCount != captureFrame)
                        FlushCapture();
                }
                catch (Exception ex)
                {
                    Fault("pump", ex);
                }
            }

            if (unpatchPending && !capturing && !ArmedFlag)
            {
                unpatchPending = false;
                Patches.GuiTreeRecorderPatches.Remove();
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
                OpenCapture();
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

        /// <summary>
        /// Everything about the captured frame that has to be read INSIDE it: the frame
        /// number, the screen the rects are measured against, and <c>GUI.matrix</c>.
        /// </summary>
        private static void OpenCapture()
        {
            captureFrame = Time.frameCount;
            try
            {
                captureScreenWidth = Screen.width;
                captureScreenHeight = Screen.height;
            }
            catch (Exception)
            {
                captureScreenWidth = 0;
                captureScreenHeight = 0;
            }

            try
            {
                Matrix4x4 m = GUI.matrix;
                matrixM00 = m.m00;
                matrixM11 = m.m11;
                matrixM03 = m.m03;
                matrixM13 = m.m13;
                matrixIsIdentity = m.isIdentity;
            }
            catch (Exception)
            {
                matrixIsIdentity = true;
                matrixM00 = 1f;
                matrixM11 = 1f;
                matrixM03 = 0f;
                matrixM13 = 0f;
            }

            if (!matrixIsIdentity)
            {
                // GUIUtility.GUIToScreenRect converts only the ORIGIN (decompiled: it
                // rewrites x and y from GUIToScreenPoint and returns w/h untouched), so
                // under a scaling GUI.matrix every recorded size is in the wrong units
                // until it is multiplied through. The recorder does that, and says so,
                // because a scaled capture is worth reading with suspicion.
                ParsekLog.Warn("GuiTree", "GUI.matrix is NOT identity at capture open"
                    + " m00=" + matrixM00.ToString("0.####", CultureInfo.InvariantCulture)
                    + " m11=" + matrixM11.ToString("0.####", CultureInfo.InvariantCulture)
                    + " m03=" + matrixM03.ToString("0.####", CultureInfo.InvariantCulture)
                    + " m13=" + matrixM13.ToString("0.####", CultureInfo.InvariantCulture)
                    + "; widths and heights are scaled by m00/m11 and the dump carries the"
                    + " matrix so a reader can undo it");
            }
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
                // Recorded from a POSTFIX, i.e. AFTER Unity pushed the clip, so the
                // depth measured here is already the one direct children will report -
                // no normalisation, which is what keeps the assembler on one rule.
                // The postfix is also what fixes an ordering bug a prefix had:
                // GUI.BeginScrollView draws its own two scrollbars BEFORE
                // GUIClip.Push, at the OUTER depth, so a scroll-view node opened
                // before them was closed again by its own scrollbar and ended up
                // holding nothing.
                events.Add(NewEvent(GuiTreeOp.Begin, kind, rect, content, style));
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        /// <summary>
        /// <c>GUILayoutUtility.BeginLayoutGroup</c> postfix. <paramref name="group"/> is
        /// the returned <c>GUILayoutGroup</c> (typed <c>object</c> because the class is
        /// internal to UnityEngine): during Repaint it is the object the LAYOUT pass
        /// created and sized, so both its rect and its <c>isVertical</c> are final.
        /// </summary>
        internal static void RecordBeginLayoutGroup(GUIStyle style, Type layoutType, object group)
        {
            try
            {
                if (!Accepting(GuiFunnel.BeginLayoutGroup))
                    return;

                bool plain = layoutType != null
                    && string.Equals(layoutType.FullName, PlainLayoutGroupTypeName,
                        StringComparison.Ordinal);
                if (!plain)
                {
                    // A GUIScrollGroup: GUI.BeginScrollView records the scroll view
                    // itself. The null keeps the End side paired without emitting a node.
                    layoutGroupStack.Add(null);
                    return;
                }

                bool horizontal = !ReadLayoutGroupIsVertical(group);
                Rect rect = ReadLayoutGroupRect(group);
                layoutGroupStack.Add(horizontal);
                GuiTreeEvent e = NewEvent(GuiTreeOp.Begin, GuiNodeKind.LayoutGroup,
                    rect, null, style);
                e.Horizontal = horizontal;
                events.Add(e);
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.BeginLayoutGroup), ex);
            }
        }

        /// <summary>
        /// <c>GUILayoutUtility.EndLayoutGroup</c> prefix. The method takes no arguments,
        /// so which group it closes comes off <see cref="layoutGroupStack"/>: a null entry
        /// is a carrier we did not record (nothing is emitted), a value is the group's
        /// orientation and rides on the End event so the assembler can tell a horizontal
        /// group's End from a vertical one's.
        /// </summary>
        internal static void RecordEndLayoutGroup()
        {
            try
            {
                if (!Accepting(GuiFunnel.EndLayoutGroup))
                    return;

                bool? horizontal = null;
                if (layoutGroupStack.Count > 0)
                {
                    horizontal = layoutGroupStack[layoutGroupStack.Count - 1];
                    layoutGroupStack.RemoveAt(layoutGroupStack.Count - 1);
                    if (!horizontal.HasValue)
                        return;
                }
                // An empty stack means the Begin was never recorded (the per-frame cap
                // tripped between the two). Emit an orientation-less End rather than
                // nothing: the assembler then matches the nearest layout group of either
                // orientation, which is still better than leaking the group open.
                events.Add(new GuiTreeEvent
                {
                    Op = GuiTreeOp.End,
                    Kind = GuiNodeKind.LayoutGroup,
                    Horizontal = horizontal,
                });
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.EndLayoutGroup), ex);
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
                // Through the same gate as every other funnel (frame fixing, the cap, the
                // hit counter), so a declaration cannot arrive from a frame the capture
                // already closed and cannot be the one event that ignores the cap.
                if (!Accepting(GuiFunnel.DoWindow))
                    return;
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
        /// that exactly brackets the window body, it is large, and it is invoked FROM
        /// native code, so it cannot be inlined at all - which makes it the load-bearing
        /// one. The declaration from <see cref="RecordWindowDeclaration"/> is merged in
        /// when present; when it is absent the node still carries an independently
        /// measured content origin and the size Unity handed the callback.
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
                Rect = ToScreenGuiRect(rect),
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
        /// <c>InternalWindowToScreenPoint(GUIClip.UnclipToWindow(p))</c> applied to the
        /// ORIGIN ONLY: decompiled, it rewrites x and y and returns width and height
        /// exactly as given. The origin walk accounts for every <c>BeginGroup</c>,
        /// <c>BeginArea</c> and <c>BeginScrollView</c> push (including a scroll view's
        /// scroll offset) and for <c>GUI.matrix</c>, then adds the window's own screen
        /// origin - which is what makes it correct inside a <c>GUI.Window</c> callback.
        /// The SIZE gets no such treatment, so a scaling matrix is applied here by hand
        /// from the m00 / m11 read when the capture opened. The raw rect is kept alongside
        /// as <c>localRect</c>, so a conversion that ever goes wrong reads as a
        /// disagreement in the dump instead of a silently misplaced box.
        /// </summary>
        private static GuiRect ToScreenGuiRect(Rect r)
        {
            Rect converted;
            try
            {
                converted = GUIUtility.GUIToScreenRect(r);
            }
            catch (Exception)
            {
                converted = r;
            }
            return new GuiRect(converted.x, converted.y,
                r.width * ScaleOrOne(matrixM00), r.height * ScaleOrOne(matrixM11));
        }

        /// <summary>A matrix scale that cannot silently zero every recorded size.</summary>
        private static float ScaleOrOne(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f)
                return 1f;
            return v;
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

        /// <summary>
        /// True when the caller is inside an IMGUI event pass. <c>Event.current</c> is a
        /// plain static field read (decompiled), so this is safe in a headless host, where
        /// it is always null.
        /// </summary>
        private static bool IsInsideGuiPass()
        {
            try
            {
                return Event.current != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Removes the interceptions, or defers that to the pump when we are inside an
        /// IMGUI pass. Never unpatch from in there: the stack is currently executing the
        /// very methods Harmony would rewrite.
        /// </summary>
        private static void RequestUnpatch()
        {
            if (IsInsideGuiPass())
            {
                unpatchPending = true;
                return;
            }
            unpatchPending = false;
            Patches.GuiTreeRecorderPatches.Remove();
        }

        // GUIClip is internal to UnityEngine.IMGUIModule and Internal_GetCount is an
        // ICall, so it can be INVOKED but never patched. Bound to a delegate rather than
        // called through MethodInfo.Invoke: this runs on EVERY recorded event, and
        // Invoke's object[] plus the boxed int return would be two allocations per
        // control. Re-resolved at every arm (see ResetBuffers).
        private static Func<int> clipCount;
        private static bool clipCountResolved;

        private static int ReadClipDepth()
        {
            try
            {
                if (!clipCountResolved)
                {
                    clipCountResolved = true;
                    Type clip = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip");
                    MethodInfo method = clip == null ? null : clip.GetMethod("Internal_GetCount",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (method != null)
                        clipCount = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
                    if (clipCount == null)
                    {
                        ParsekLog.Warn("GuiTree",
                            "clip-depth probe unavailable (UnityEngine.GUIClip.Internal_GetCount not found); "
                            + "tree nesting falls back to Begin/End pairing only");
                    }
                }
                if (clipCount == null)
                    return -1;
                return clipCount();
            }
            catch (Exception)
            {
                // Do NOT park the probe: a transient failure on one event must not cost
                // every later event in this capture its depth. The event just reports
                // "unknown", which the assembler already handles.
                return -1;
            }
        }

        // GUILayoutGroup and its GUILayoutEntry base are internal, so a layout group's
        // rect and orientation are only reachable by reflection over the instance the
        // BeginLayoutGroup postfix hands us. Re-resolved at every arm (see ResetBuffers).
        private static FieldInfo layoutEntryRectField;
        private static FieldInfo layoutGroupIsVerticalField;
        private static bool layoutProbeResolved;

        private static void ResolveLayoutProbe()
        {
            if (layoutProbeResolved)
                return;
            layoutProbeResolved = true;
            Assembly unity = typeof(GUI).Assembly;
            Type entry = unity.GetType("UnityEngine.GUILayoutEntry");
            Type group = unity.GetType("UnityEngine.GUILayoutGroup");
            if (entry != null)
            {
                layoutEntryRectField = entry.GetField("rect",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (group != null)
            {
                layoutGroupIsVerticalField = group.GetField("isVertical",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (layoutEntryRectField == null || layoutGroupIsVerticalField == null)
            {
                ParsekLog.Warn("GuiTree",
                    "layout-group probe unavailable (rectField="
                    + (layoutEntryRectField != null) + " isVerticalField="
                    + (layoutGroupIsVerticalField != null)
                    + "); layout groups record a zero rect and read as vertical");
            }
        }

        private static Rect ReadLayoutGroupRect(object group)
        {
            try
            {
                ResolveLayoutProbe();
                if (layoutEntryRectField == null || group == null)
                    return new Rect(0f, 0f, 0f, 0f);
                object value = layoutEntryRectField.GetValue(group);
                return value is Rect ? (Rect)value : new Rect(0f, 0f, 0f, 0f);
            }
            catch (Exception)
            {
                return new Rect(0f, 0f, 0f, 0f);
            }
        }

        private static bool ReadLayoutGroupIsVertical(object group)
        {
            try
            {
                ResolveLayoutProbe();
                if (layoutGroupIsVerticalField == null || group == null)
                    return true;
                object value = layoutGroupIsVerticalField.GetValue(group);
                return !(value is bool) || (bool)value;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Logs once and disarms. An IMGUI pass that already started must never see an
        /// exception from a patch body, so the recorder gives up rather than retrying.
        /// The interceptions come off too - deferred to the pump, because a fault is
        /// normally raised from inside the GUI pass that is running them.
        /// </summary>
        private static void Fault(string funnel, Exception ex)
        {
            recordFaults++;
            LastRecordFaults = recordFaults;
            ArmedFlag = false;
            RequestUnpatch();
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

        /// <summary>
        /// Assembles, serialises and writes the captured frame. Runs from LateUpdate, one
        /// frame after the capture, and is deliberately ONE SYNCHRONOUS PASS: a few
        /// hundred small objects assembled, a string built and a file written, all inside
        /// one LateUpdate. That is a visible hitch on a big window, and it is accepted -
        /// a capture is a deliberate one-off, and splitting it across frames would mean
        /// holding the event buffer into a frame where the UI has already moved on.
        /// </summary>
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
                ScreenWidth = captureScreenWidth,
                ScreenHeight = captureScreenHeight,
                ScreenshotHint = (pendingLabel ?? "capture") + ".png",
                RecordFaults = recordFaults,
                DroppedOverCap = droppedOverCap,
                MatrixIsIdentity = matrixIsIdentity,
                MatrixM00 = matrixM00,
                MatrixM11 = matrixM11,
                MatrixM03 = matrixM03,
                MatrixM13 = matrixM13,
            };
            for (int i = 0; i < GuiTreeFunnels.Count; i++)
            {
                var funnel = (GuiFunnel)i;
                header.Funnels.Add(new GuiTreeFunnelReport(
                    GuiTreeFunnels.Name(funnel), GuiTreeFunnels.PatchedAtArm[i], GuiTreeFunnels.Hits[i]));
            }

            LastTree = tree;
            LastRecordFaults = recordFaults;
            LastDroppedOverCap = droppedOverCap;
            LastMatrixNonIdentity = !matrixIsIdentity;
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
                + " rectRuleInert=" + tree.RectRuleInert.ToString(CultureInfo.InvariantCulture)
                + " unclosed=" + tree.UnclosedAtEnd.ToString(CultureInfo.InvariantCulture)
                + " faults=" + recordFaults.ToString(CultureInfo.InvariantCulture)
                + " written=" + (written ? "1" : "0")
                + " path=" + path);

            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            pendingControl = null;

            // The capture is over, so the interceptions come off. FlushCapture runs from
            // LateUpdate, outside any GUI pass, so this is done here and now.
            RequestUnpatch();
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
    /// capture can be assembled and written - and when the Harmony interceptions can be
    /// removed - without doing either inside an IMGUI pass.
    /// Not a UI surface: no OnGUI, nothing drawn, nothing shown to the player.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class GuiTreeRecorderPump : MonoBehaviour
    {
        void LateUpdate()
        {
            if (!GuiTreeRecorder.HasPendingWork)
                return;
            GuiTreeRecorder.PumpPendingFlush();
        }
    }
}
