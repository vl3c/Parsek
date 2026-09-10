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

        /// <summary>
        /// Frames the recorder stays armed waiting for a Repaint funnel to fire before it
        /// gives up by itself.
        ///
        /// <para><b>Why a bound is required.</b> Nothing else disarms an arm that never
        /// sees a Repaint - a scene where no IMGUI consumer draws, a window that stopped
        /// being drawn between the arm and the pass - and the recorder's whole cost story
        /// is that the Harmony detours come OFF again. Without this they would stay
        /// installed on all 17 IMGUI funnels for the rest of the process, which is exactly
        /// the permanent cost the opt-in design exists to avoid.</para>
        ///
        /// <para>Sized well past the live in-game cell's own 300-frame wait after arming,
        /// so the give-up can never fire inside a wait a caller is legitimately
        /// performing.</para>
        /// </summary>
        internal const int ArmTimeoutFrames = 900;

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

        /// <summary>
        /// <c>Time.frameCount</c> when the recorder was armed, or -1 when it is not armed
        /// or the frame count could not be read (a headless host). Only used by the
        /// armed-with-no-Repaint give-up, whose decision treats -1 as "no timeout".
        /// </summary>
        private static int armFrame = -1;
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
        /// Screen rects of the clip containers currently between their Begin funnel's
        /// PREFIX and its POSTFIX, innermost last. Containers nest, so it is a stack.
        ///
        /// <para><b>Why the container's own rect cannot be converted at postfix time.</b>
        /// Decompiled from the shipped <c>UnityEngine.IMGUIModule.dll</c>,
        /// <c>GUI.BeginGroup</c> ENDS with
        /// <c>GUIClip.Push(position, scrollOffset, Vector2.zero, false)</c> and
        /// <c>GUI.BeginScrollView</c> with
        /// <c>GUIClip.Push(screenRect, (round(-scroll.x - viewRect.x), round(-scroll.y -
        /// viewRect.y)), Vector2.zero, false)</c>. So by the time the postfix runs, the
        /// container's OWN clip is topmost, and <c>GUIClip.UnclipToWindow</c> - which
        /// <c>GUIUtility.GUIToScreenRect</c> walks - adds the container's origin (and, for
        /// a scroll view, its scroll offset) a SECOND time. The prefix therefore converts
        /// the rect BEFORE the push and stashes it here; the postfix still reads
        /// everything else there, because the clip depth measured after the push is the
        /// one the container's children report.</para>
        ///
        /// <para>Cleared wherever the buffers are (arm, flush, disarm, fault), so an
        /// aborted pass cannot leave an entry standing for the next capture.</para>
        /// </summary>
        private static readonly List<GuiRect> containerScreenRects = new List<GuiRect>(8);

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

        /// <summary>
        /// Why the LAST <see cref="ArmForNextRepaint"/> call refused, or null when it
        /// armed. Cleared at the start of every arm, so it always describes the most
        /// recent request and never an older one.
        ///
        /// <para>Exists because the arm reports a refusal by returning null and LOGGING
        /// the reason, which is enough for a person reading KSP.log and not enough for the
        /// command seam: a verb whose terminal says only "refused" costs a whole KSP boot
        /// to diagnose. The <c>DumpGuiTree</c> verb puts this string on its ERROR message
        /// (<c>gui-tree-arm-refused reason=inside-gui-pass</c>).</para>
        /// </summary>
        internal static string LastArmRefusalReason { get; private set; }

        /// <summary>
        /// The reason passed to the last <see cref="Disarm"/> since the last arm, or null
        /// when none has run. <c>armed-no-repaint</c> is the one a caller cares about: it
        /// is the pump giving the arm up because nothing drew IMGUI through a patched
        /// funnel within <see cref="ArmTimeoutFrames"/>.
        ///
        /// <para>It distinguishes the two ways a poll can find the recorder idle with no
        /// dump: that give-up, and a flush whose <c>File.WriteAllText</c> threw (which logs
        /// its own Error and leaves this null). The seam's terminal is the same for both -
        /// there is no dump either way - but the log line says which.</para>
        /// </summary>
        internal static string LastDisarmReason { get; private set; }

        /// <summary>
        /// The <see cref="Disarm"/> reason for an arm that THREW after
        /// <c>GuiTreeRecorderPatches.Apply()</c> had run. One constant so the recorder's
        /// own guard and the seam verb's catch (<c>ParsekTestCommandAddon.DumpGuiTree</c>)
        /// cannot drift into two spellings of the same event in KSP.log.
        /// </summary>
        internal const string ArmThrewDisarmReason = "arm-threw";

        /// <summary>
        /// Patch-body exceptions counted SINCE THE LAST ARM. Cleared by
        /// <see cref="ResetBuffers"/> on every arm, which is what makes it a statement
        /// about THIS capture - unlike <see cref="LastRecordFaults"/>, which is the last
        /// COMPLETED capture's reading and survives the next arm until that capture
        /// flushes.
        ///
        /// <para>A fault does not necessarily mean no dump: <see cref="Fault"/> clears
        /// <see cref="ArmedFlag"/> but leaves an OPEN capture for the pump to flush, so a
        /// partial tree can still reach disk. That is why the seam reads this rather than
        /// inferring "faulted" from a missing file.</para>
        /// </summary>
        internal static int FaultsSinceArm
        {
            get { return recordFaults; }
        }

        /// <summary>True while a captured frame is waiting to be flushed by the pump.</summary>
        internal static bool HasPendingFlush
        {
            get { return capturing; }
        }

        /// <summary>
        /// True while the LateUpdate pump has something to do: a capture to flush, a
        /// deferred unpatch to perform, or an ARM to time out.
        ///
        /// <para><see cref="ArmedFlag"/> is in here deliberately. An arm whose Repaint
        /// never arrives leaves no capture and no pending unpatch, so a pump gated on
        /// those two alone never ran again and the detours stayed installed for the
        /// session (see <see cref="ArmTimeoutFrames"/>). The cost of including it is two
        /// static reads and a frame-count read per frame while armed, for the handful of
        /// frames an arm normally lives.</para>
        /// </summary>
        internal static bool HasPendingWork
        {
            get { return capturing || unpatchPending || ArmedFlag; }
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
        /// Sentinel <see cref="ReadGuiDepth"/> returns when the depth probe is
        /// unavailable - the member did not resolve, or invoking the ICall threw (which is
        /// what a headless host does).
        /// </summary>
        internal const int GuiDepthUnavailable = -1;

        /// <summary>
        /// "Is the caller inside an IMGUI pass", from a <c>GUIUtility.guiDepth</c> reading.
        /// Pure, so both the predicate and its FALLBACK are pinned without a Unity GUI
        /// pass.
        ///
        /// <para><b>The fallback is "not inside"</b>
        /// (<see cref="GuiDepthUnavailable"/> is negative). Both call sites are outside a
        /// GUI pass BY CONSTRUCTION - the seam / coroutine / Update that arms, and the
        /// LateUpdate pump that unpatches - so the guard is defensive only, and refusing
        /// on an unreadable depth would disable the whole feature exactly the way the
        /// <c>Event.current != null</c> guard it replaced did. An unresolvable probe logs
        /// one Warn (<see cref="ReadGuiDepth"/>) so a flight can see which predicate was
        /// used.</para>
        /// </summary>
        internal static bool ClassifyInsideGuiPass(int guiDepthReading)
        {
            return guiDepthReading > 0;
        }

        /// <summary>
        /// Whether an arm that has not seen a single Repaint funnel yet must be given up
        /// on. Pure, so both the bound and its unknown-frame case are pinned headlessly.
        ///
        /// <para><paramref name="framesSinceArm"/> is negative when the arm frame could
        /// not be read (a host with no <c>Time.frameCount</c>), and that is NOT a timeout:
        /// giving up on an unreadable clock would disarm every capture on such a host
        /// before it recorded anything. A non-positive budget disables the give-up
        /// outright.</para>
        /// </summary>
        internal static bool ClassifyArmTimeout(int framesSinceArm, int budgetFrames)
        {
            if (framesSinceArm < 0 || budgetFrames <= 0)
                return false;
            return framesSinceArm >= budgetFrames;
        }

        /// <summary>
        /// Arms the recorder for the next IMGUI Repaint pass and returns the path the dump
        /// will be written to, or null when the request was refused. Re-arming discards
        /// any capture in progress.
        /// </summary>
        internal static string ArmForNextRepaint(string label)
        {
            // Re-resolved here rather than in ResetBuffers like the other reflection
            // probes: this one is READ before the buffers are cleared, so resetting it
            // there would leave the guard on the previous arm's binding.
            ResetGuiDepthProbe();
            int depthAtArm = ReadGuiDepth();
            string refusal = ClassifyArmRefusal(ClassifyInsideGuiPass(depthAtArm));
            // Both reasons are per-arm state: cleared here so a caller polling them can
            // never read a previous arm's refusal or a previous arm's give-up.
            LastArmRefusalReason = refusal;
            LastDisarmReason = null;
            if (refusal != null)
            {
                ParsekLog.Warn("GuiTree", "arm refused reason=" + refusal
                    + " guiDepth=" + depthAtArm.ToString(CultureInfo.InvariantCulture)
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

            // EVERY PATH OUT OF THE REGION BELOW MUST END ARMED OR UNPATCHED, and this
            // try/catch is what makes that true. Once Apply() has run the interceptions
            // are installed (possibly only some of them - it catches per funnel and
            // reports `failed=`), and the only two things that ever take them off are the
            // flush of a capture and Disarm's RequestUnpatch. Neither can be reached
            // without ArmedFlag: the pump's give-up branch runs only while armed, and its
            // deferred-unpatch branch only while unpatchPending, which the line below
            // clears. So a throw anywhere between Apply() and `ArmedFlag = true` - out of
            // Apply's own tail after it set Applied, or out of the funnel readback - would
            // leave 17 detours installed with the pump idle for the rest of the session.
            // The exception is RETHROWN: the caller decides the verdict (the seam verb
            // reports gui-tree-faulted), this method only guarantees the cleanup.
            try
            {
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
                armFrame = ReadFrameCount();
                ParsekLog.Info("GuiTree", "armed label=" + safe
                    // The predicate the arm guard actually used: 0 is "outside OnGUI, from
                    // GUIUtility.guiDepth", -1 is "probe unavailable, fell back to outside".
                    + " guiDepth=" + depthAtArm.ToString(CultureInfo.InvariantCulture)
                    + " patchedFunnels=" + patched.ToString(CultureInfo.InvariantCulture)
                    + "/" + GuiTreeFunnels.Count.ToString(CultureInfo.InvariantCulture)
                    + " path=" + pendingPath);
                return pendingPath;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("GuiTree", "arm threw after applying the interceptions label="
                    + safe + ": " + ex.GetType().Name + ": " + ex.Message
                    + "; disarming so they come off rather than leaving them installed "
                    + "with nothing left to remove them");
                Disarm(ArmThrewDisarmReason);
                throw;
            }
        }

        /// <summary>Disarms without writing anything. Safe to call at any time.</summary>
        internal static void Disarm(string reason)
        {
            bool wasArmed = ArmedFlag || capturing;
            LastDisarmReason = reason;
            ArmedFlag = false;
            capturing = false;
            captureFrame = -1;
            armFrame = -1;
            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            containerScreenRects.Clear();
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
            LastArmRefusalReason = null;
            LastDisarmReason = null;
            faultLogged = false;
            unpatchPending = false;
            ResetGuiDepthProbe();
        }

        private static void ResetBuffers()
        {
            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            containerScreenRects.Clear();
            pendingControl = null;
            capturing = false;
            captureFrame = -1;
            armFrame = -1;
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
            // Cleared here and re-snapshotted right after Apply(), so a refused or
            // aborted arm can never leave a previous capture's reading standing.
            Array.Clear(GuiTreeFunnels.PatchedAtArm, 0, GuiTreeFunnels.Count);
            // Re-resolve the reflection probes on every arm rather than parking them for
            // the process lifetime: a probe that failed once - a transient during scene
            // load, a type not yet loaded - must not silently cost every later capture
            // its clip depths or its layout-group rects.
            clipCountResolved = false;
            clipCount = null;
            LastClipProbeBinding = null;
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
            else if (ArmedFlag)
            {
                // Armed, and not one Repaint funnel has fired. Give up after a bounded
                // number of frames rather than leaving the interceptions installed for
                // the rest of the session: nothing else would ever take them off.
                int framesSinceArm = FramesSinceArm();
                if (ClassifyArmTimeout(framesSinceArm, ArmTimeoutFrames))
                {
                    ParsekLog.Warn("GuiTree", "arm gave up: no Repaint funnel fired within "
                        + framesSinceArm.ToString(CultureInfo.InvariantCulture)
                        + " frames of arming (budget "
                        + ArmTimeoutFrames.ToString(CultureInfo.InvariantCulture)
                        + "); disarming so the interceptions come off. Nothing drew IMGUI"
                        + " through a patched funnel - a scene with no IMGUI consumer, or"
                        + " every funnel's signature drifted");
                    Disarm("armed-no-repaint");
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
            return Accepting(funnel, true);
        }

        /// <summary>
        /// <see cref="Accepting(GuiFunnel)"/> with the hit counter under caller control.
        ///
        /// <para><paramref name="countHit"/> is false for the SECOND entry point of a
        /// funnel that has both a prefix and a postfix feeding the recorder -
        /// <c>GUI.CallWindowDelegate</c>, whose prefix opens the window node and whose
        /// postfix closes it. <c>hits</c> is documented as one count per funnel body run,
        /// and counting both ends made the window funnel read exactly double.</para>
        /// </summary>
        private static bool Accepting(GuiFunnel funnel, bool countHit)
        {
            if (!ArmedFlag)
                return false;
            // Event.current is SAFE here, unlike in the arm / unpatch guard: every caller
            // of Accepting is a patch body on an IMGUI funnel, so this only ever runs
            // inside a GUI pass, where Event.current is the pass's real event and its
            // .type is the reading we want. What made it wrong as an "am I inside OnGUI"
            // predicate - the master event staying installed after the pass - cannot
            // mislead a Repaint check reached only from inside a pass. The null guard is
            // kept as belt-and-braces for a host that never drew a frame.
            Event current = Event.current;
            if (current == null || current.type != EventType.Repaint)
                return false;

            // Counted here rather than at the call site: a hit only means anything for
            // the Repaint pass the dump describes, and counting Layout passes too would
            // make every funnel row read roughly double its node count.
            if (countHit)
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
        /// The armed-and-Repaint half of <see cref="Accepting(GuiFunnel)"/>, with NO hit
        /// counter, no capture open and no cap. For a patch body that records no node of
        /// its own and only stages something for another body - today the clip-container
        /// prefixes, which convert a rect before their container pushes its clip.
        /// </summary>
        private static bool ArmedForRepaintWithoutCounting()
        {
            if (!ArmedFlag)
                return false;
            // Same reasoning as in Accepting: every caller is a patch body on an IMGUI
            // funnel, so this only runs inside a GUI pass, where Event.current is that
            // pass's own event.
            Event current = Event.current;
            return current != null && current.type == EventType.Repaint;
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

        /// <summary>
        /// The rect a clip container's node carries: the ORIGIN from the conversion the
        /// PREFIX made before the container pushed its own clip, and the SIZE from the
        /// postfix. Pure, so the corrected contract is pinned headlessly.
        ///
        /// <para><b>Why the two halves come from different sides.</b>
        /// <c>GUIUtility.GUIToScreenRect</c> converts the ORIGIN ONLY and returns width
        /// and height exactly as given, so the origin is the only half the container's own
        /// clip can corrupt. The size is scaled by hand from the <c>GUI.matrix</c> read
        /// when the capture OPENS - and the prefix is gated so it does NOT open the
        /// capture, so a container that happens to be the first funnel event of a capture
        /// would have had its size scaled by the reset defaults. Taking the size from the
        /// postfix, which runs after <c>Accepting</c> opened the capture, keeps a scaled
        /// matrix correct for that container too.</para>
        ///
        /// <para>With no prefix reading at all the whole postfix rect is the fallback -
        /// which is the double-counted one, and unreachable in the live path, since prefix
        /// and postfix are hooks on the SAME method.</para>
        /// </summary>
        internal static GuiRect ResolveContainerScreenRect(GuiRect? pushedAtPrefix,
            GuiRect measuredAtPostfix)
        {
            if (!pushedAtPrefix.HasValue)
                return measuredAtPostfix;
            return new GuiRect(pushedAtPrefix.Value.X, pushedAtPrefix.Value.Y,
                measuredAtPostfix.W, measuredAtPostfix.H);
        }

        /// <summary>
        /// <c>GUI.BeginGroup</c> / <c>GUI.BeginScrollView</c> PREFIX: converts the
        /// container's own rect while the container's clip is not yet on the clip stack,
        /// and stashes it for the postfix that records the node.
        ///
        /// <para>Gated by <see cref="ArmedForRepaintWithoutCounting"/> rather than
        /// <see cref="Accepting(GuiFunnel)"/> ON PURPOSE: this emits no node, so counting
        /// it would double the container funnel's <c>hits</c>, and opening the capture
        /// from here would fix <c>captureFrame</c> on an event that records nothing.</para>
        /// </summary>
        internal static void PushContainerScreenRect(GuiFunnel funnel, Rect position)
        {
            try
            {
                if (!ArmedForRepaintWithoutCounting())
                    return;
                containerScreenRects.Add(ToScreenGuiRect(position));
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(funnel), ex);
            }
        }

        private static GuiRect? PopContainerScreenRect()
        {
            int last = containerScreenRects.Count - 1;
            if (last < 0)
                return null;
            GuiRect popped = containerScreenRects[last];
            containerScreenRects.RemoveAt(last);
            return popped;
        }

        internal static void RecordBeginClipContainer(GuiFunnel funnel, GuiNodeKind kind,
            Rect rect, GUIContent content, GUIStyle style)
        {
            try
            {
                // Popped FIRST, and unconditionally: the prefix pushed under a laxer gate
                // than Accepting (no per-frame cap, no capture-frame check), so a postfix
                // that Accepting refuses must still balance the stack or the NEXT
                // container would read this one's origin.
                GuiRect? prefixRect = PopContainerScreenRect();
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
                GuiTreeEvent e = NewEvent(GuiTreeOp.Begin, kind, rect, content, style);
                // ...but the RECT is the one thing the postfix cannot measure: the clip
                // it would walk is the container's own. Everything else stays as read
                // here - the clip depth is the children's depth by construction, and
                // GUI.enabled / text / style are the state the container opened in.
                // LocalRect stays the raw position the funnel received.
                e.Rect = ResolveContainerScreenRect(prefixRect, e.Rect);
                events.Add(e);
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
        ///
        /// <para><b>The clip-container rect trap does NOT apply here</b>, which is the
        /// mirror direction of that fix. Decompiled, <c>BeginLayoutGroup</c> ends with
        /// <c>current.layoutGroups.Push(group); current.topLevel = group;</c> and touches
        /// <c>GUIClip</c> nowhere - a layout group pushes no clip at all, which is exactly
        /// why the assembler recovers its close by rect containment. So converting the
        /// group's rect in this postfix adds nothing twice.</para>
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
        /// <c>GUI.CallWindowDelegate</c> POSTFIX: closes the window node this funnel's
        /// prefix opened. Does NOT count a hit - the prefix already counted the one run of
        /// this funnel body, and a per-funnel <c>hits</c> that counts both ends of the same
        /// call reads exactly double.
        /// </summary>
        internal static void RecordWindowEnd()
        {
            try
            {
                if (!Accepting(GuiFunnel.CallWindowDelegate, false))
                    return;
                events.Add(new GuiTreeEvent
                {
                    Op = GuiTreeOp.End,
                    Kind = GuiNodeKind.Window,
                });
            }
            catch (Exception ex)
            {
                Fault(GuiTreeFunnels.Name(GuiFunnel.CallWindowDelegate), ex);
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
        ///
        /// <para><b>The window is the third mirror of the container-rect trap, and it is
        /// correct as written.</b> The native side pushes the window's clip BEFORE it
        /// invokes this callback, so a conversion here does sit under a clip nobody in
        /// managed code pushed - but nothing here converts a rect the window declared.
        /// <see cref="ScreenPointOfCurrentOrigin"/> converts <c>Vector2.zero</c>, i.e. the
        /// window's own content origin expressed in the space the children are drawn in,
        /// so unclipping it through the window's clip is the whole POINT: it yields where
        /// the content origin actually landed on screen. <c>argWidth</c> / <c>argHeight</c>
        /// are Unity's own numbers, and the declared rect comes from
        /// <see cref="RecordWindowDeclaration"/> - a <c>GUI.DoWindow</c> PREFIX, which
        /// records it unconverted.</para>
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

        /// <summary>
        /// <c>Time.frameCount</c>, or -1 when it cannot be read (a headless host, where
        /// the property is a Unity ICall). Never throws: it is read from the arm path and
        /// from the pump.
        /// </summary>
        private static int ReadFrameCount()
        {
            try
            {
                return Time.frameCount;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Frames elapsed since the arm, or -1 when either reading is unavailable - which
        /// <see cref="ClassifyArmTimeout"/> treats as "no timeout".
        /// </summary>
        private static int FramesSinceArm()
        {
            if (armFrame < 0)
                return -1;
            int now = ReadFrameCount();
            return now < 0 ? -1 : now - armFrame;
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
        /// True when the caller is inside an IMGUI event pass, read from
        /// <c>GUIUtility.guiDepth</c> - Unity's own predicate for this (see
        /// <see cref="GuiTreeFunnels.GuiDepthGetter"/>).
        ///
        /// <para><b>NOT <c>Event.current != null</c>, which is what this guard used to be
        /// and which is always true.</b> Decompiled from the shipped
        /// <c>UnityEngine.IMGUIModule.dll</c>: the getter is <c>return s_Current;</c> with
        /// no depth gating, <c>Internal_MakeMasterEventCurrent</c> assigns
        /// <c>s_MasterEvent</c> to <c>s_Current</c> on the first GUI pass, and the setter
        /// maps a null assignment back to the master event
        /// (<c>s_Current = value ?? s_MasterEvent;</c>). Nothing but
        /// <c>Event.CleanupRoots</c> ever nulls it again, so in the player
        /// <c>Event.current</c> is non-null forever after the first frame - which refused
        /// every arm and made the whole feature dead on arrival.</para>
        /// </summary>
        private static bool IsInsideGuiPass()
        {
            return ClassifyInsideGuiPass(ReadGuiDepth());
        }

        // GUIUtility.guiDepth is internal AND an ICall, so it is reachable only by
        // reflection and can never be patched. Called through MethodInfo.Invoke rather
        // than a bound delegate, unlike clipCount: this guard is read at most twice per
        // capture (the arm, and the unpatch) instead of once per recorded control, so the
        // boxed int costs nothing - and Delegate.CreateDelegate over an ECall is itself
        // refused by the CLR outside the module that declares it ("ECall methods must be
        // packaged into a system module", which is what the xUnit host raises). Invoke has
        // no such restriction. Re-resolved at every arm (see ArmForNextRepaint).
        private static MethodInfo guiDepthGetter;
        private static bool guiDepthResolved;
        private static bool guiDepthWarned;

        private static void ResetGuiDepthProbe()
        {
            guiDepthResolved = false;
            guiDepthGetter = null;
            guiDepthWarned = false;
        }

        /// <summary>
        /// The current OnGUI depth, or <see cref="GuiDepthUnavailable"/> when the probe
        /// cannot be resolved or invoking it throws - which is what a HEADLESS host does,
        /// since the getter bottoms out in a Unity ICall. Never throws: it is reached from
        /// <see cref="Fault"/> via <see cref="RequestUnpatch"/>, i.e. from inside an OnGUI
        /// pass that must not see an exception.
        /// </summary>
        private static int ReadGuiDepth()
        {
            try
            {
                if (!guiDepthResolved)
                {
                    guiDepthResolved = true;
                    guiDepthGetter = GuiTreeFunnels.GuiDepthGetter();
                    if (guiDepthGetter == null)
                        WarnGuiDepthFallback("UnityEngine.GUIUtility.guiDepth did not resolve");
                }
                if (guiDepthGetter == null)
                    return GuiDepthUnavailable;
                object value = guiDepthGetter.Invoke(null, null);
                if (!(value is int))
                {
                    WarnGuiDepthFallback("UnityEngine.GUIUtility.guiDepth returned "
                        + (value == null ? "null" : value.GetType().Name));
                    return GuiDepthUnavailable;
                }
                return (int)value;
            }
            catch (Exception ex)
            {
                WarnGuiDepthFallback(ex.GetType().Name + ": " + ex.Message);
                return GuiDepthUnavailable;
            }
        }

        /// <summary>One Warn per arm, and never an exception of its own.</summary>
        private static void WarnGuiDepthFallback(string why)
        {
            try
            {
                if (guiDepthWarned)
                    return;
                guiDepthWarned = true;
                ParsekLog.Warn("GuiTree", "gui-depth probe unavailable (" + (why ?? "unknown")
                    + "); the inside-OnGUI guard falls back to NOT inside, so an arm proceeds"
                    + " and an unpatch runs immediately. Both call sites are outside the GUI"
                    + " pass by construction (the seam / coroutine that arms, and the"
                    + " LateUpdate pump), so the guard is defensive only");
            }
            catch (Exception)
            {
                // Reachable from inside OnGUI; there is nothing safe left to do.
            }
        }

        /// <summary>
        /// Removes the interceptions, or defers that to the pump when we are inside an
        /// IMGUI pass (<see cref="IsInsideGuiPass"/>, i.e. <c>GUIUtility.guiDepth &gt; 0</c>).
        /// Never unpatch from in there: the stack is currently executing the very methods
        /// Harmony would rewrite. The deferral is what <see cref="Fault"/> and
        /// <see cref="Disarm"/> rely on; <see cref="FlushCapture"/> already runs in
        /// LateUpdate, so it unpatches at once.
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
        // ICall, so it can be INVOKED but never patched. Bound DELEGATE-FIRST because it
        // runs on EVERY recorded event and MethodInfo.Invoke's object[] plus boxed int
        // return would be two allocations per control - but Delegate.CreateDelegate over
        // an ECall is refused outside the declaring module on the Windows CLR
        // (SecurityException, "ECall methods must be packaged into a system module", which
        // is what the xUnit host raises for the sibling guiDepth probe) and mono may or
        // may not accept it either. So a refusal falls back to an Invoke wrapper rather
        // than losing the depth for the whole capture. Re-resolved at every arm (see
        // ResetBuffers).
        private static Func<int> clipCount;
        private static bool clipCountResolved;

        /// <summary>Which binding the clip probe got: "delegate", "invoke" or "none".</summary>
        internal const string ClipProbeBindingDelegate = "delegate";
        internal const string ClipProbeBindingInvoke = "invoke";
        internal const string ClipProbeBindingNone = "none";

        /// <summary>
        /// The binding the clip-depth probe last resolved to, for the arm / capture log
        /// and for the tests. Null until a capture reads a depth.
        /// </summary>
        internal static string LastClipProbeBinding { get; private set; }

        /// <summary>
        /// Binds a zero-argument int method as a <c>Func&lt;int&gt;</c> through
        /// <c>Delegate.CreateDelegate</c>, or null when the CLR refuses - which is the
        /// documented behaviour for an ECall outside its declaring module.
        /// </summary>
        internal static Func<int> BindIntProbeViaDelegate(MethodInfo method)
        {
            if (method == null)
                return null;
            try
            {
                return (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The fallback binding: a wrapper over <c>MethodInfo.Invoke</c>, which has none
        /// of <c>CreateDelegate</c>'s restrictions, at the cost of an <c>object[]</c> and
        /// a boxed return per call. A non-int return reads as unknown (-1) rather than
        /// throwing into the GUI pass.
        /// </summary>
        internal static Func<int> BindIntProbeViaInvoke(MethodInfo method)
        {
            if (method == null)
                return null;
            MethodInfo bound = method;
            return delegate
            {
                object value = bound.Invoke(null, null);
                return value is int ? (int)value : -1;
            };
        }

        /// <summary>
        /// Delegate-first, Invoke-fallback binding for a zero-argument int probe. Returns
        /// null only when the member itself did not resolve; <paramref name="binding"/>
        /// names the path taken so a flight can read it off the log.
        /// </summary>
        internal static Func<int> BindIntProbe(MethodInfo method, out string binding)
        {
            if (method == null)
            {
                binding = ClipProbeBindingNone;
                return null;
            }
            Func<int> bound = BindIntProbeViaDelegate(method);
            if (bound != null)
            {
                binding = ClipProbeBindingDelegate;
                return bound;
            }
            binding = ClipProbeBindingInvoke;
            return BindIntProbeViaInvoke(method);
        }

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
                    string binding;
                    clipCount = BindIntProbe(method, out binding);
                    LastClipProbeBinding = binding;
                    if (clipCount == null)
                    {
                        ParsekLog.Warn("GuiTree",
                            "clip-depth probe unavailable (UnityEngine.GUIClip.Internal_GetCount not found); "
                            + "tree nesting falls back to Begin/End pairing only");
                    }
                    else
                    {
                        // Which path bound is worth one line: the delegate path is the
                        // cheap one, and the Invoke path allocates twice per recorded
                        // control - a capture that reads "invoke" explains its own cost.
                        ParsekLog.Verbose("GuiTree",
                            "clip-depth probe bound binding=" + binding
                            + (binding == ClipProbeBindingInvoke
                                ? " (Delegate.CreateDelegate refused the ECall; MethodInfo.Invoke"
                                    + " costs an object[] and a boxed int per recorded control)"
                                : string.Empty));
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
            armFrame = -1;
            // A fault normally arrives mid-pass with container prefixes outstanding, so
            // the stashed rects go with the arm rather than waiting for the next capture.
            try
            {
                containerScreenRects.Clear();
            }
            catch (Exception)
            {
                // Inside OnGUI; nothing safe left to do.
            }
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
            armFrame = -1;
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
                // Which binding the per-event clip probe got: "delegate" is the cheap
                // path, "invoke" the ECall fallback, "none" means no depths at all.
                + " clipProbe=" + (LastClipProbeBinding ?? "unread")
                + " written=" + (written ? "1" : "0")
                + " path=" + path);

            events.Clear();
            pendingWindows.Clear();
            layoutGroupStack.Clear();
            containerScreenRects.Clear();
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
