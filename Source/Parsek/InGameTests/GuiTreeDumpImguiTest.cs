using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI proof that <see cref="GuiTreeRecorder"/> captures a REAL Repaint pass:
    /// a probe MonoBehaviour draws a window of known shape, the recorder is armed for one
    /// frame, and the written JSON is read back and checked for those controls with that
    /// nesting and with screen rects inside the window rect.
    ///
    /// <para><b>Why this cannot be a unit test.</b> The pure half - event stream to tree
    /// to JSON - is fully covered headlessly by <c>GuiTreeAssemblerTests</c> and
    /// <c>GuiTreeJsonTests</c>. What no headless test can reach is the half that matters
    /// for feasibility: whether Harmony's interception of private UnityEngine IMGUI
    /// methods actually fires (Mono is free to inline any of them into its caller and
    /// silently bypass the patch), whether <c>GUIUtility.GUIToScreenRect</c> converts
    /// correctly INSIDE a <c>GUI.Window</c> callback, and whether the clip-depth probe on
    /// the internal <c>GUIClip</c> resolves at all. All three are measurements on the
    /// machine the mod runs on.</para>
    ///
    /// <para>Its OWN category, deliberately: adding a cell to an existing category moves
    /// the <c>BATCH_COMPLETE</c> tally that committed harness specs pin, and
    /// <c>CommittedBatchTallySourceSyncTests</c> reds the harness suite for it. A category
    /// no spec drives keeps the tally honest until someone flies this one - the same
    /// reasoning as <c>DisabledHoverEcho</c>.</para>
    /// </summary>
    public sealed class GuiTreeDumpImguiTest
    {
        private const string CaptureLabel = "parsek-guitree-probe";

        [InGameTest(Category = "GuiTree",
            Description = "Arming the GUI tree recorder for one Repaint captures a probe window's real control tree - window, vertical group, label, button, toggle, text field and a scroll view with three labels - into JSON with screen rects inside the window")]
        public IEnumerator ArmedRepaint_CapturesTheProbeWindowsControlTree()
        {
            var go = new GameObject("ParsekGuiTreeProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            GuiTreeProbe probe = go.AddComponent<GuiTreeProbe>();
            string path = null;

            try
            {
                // Let the probe settle so its window has been through a Layout pass and
                // owns a stable rect before anything is captured.
                int settle = 0;
                while (probe.RepaintPasses < 3 && settle < 240)
                {
                    settle++;
                    yield return null;
                }

                if (probe.RepaintPasses == 0)
                {
                    InGameAssert.Skip(
                        "the probe never observed an IMGUI Repaint pass (frames=" + settle
                        + "); nothing can be captured in this context");
                    yield break;
                }

                InGameAssert.IsFalse(probe.Faulted,
                    "the probe window threw while drawing, before any capture: " + probe.FaultMessage);

                path = GuiTreeRecorder.ArmForNextRepaint(CaptureLabel);
                InGameAssert.IsNotNull(path, "ArmForNextRepaint returned no output path");

                int waited = 0;
                while (!GuiTreeRecorder.LastCaptureWritten && waited < 300)
                {
                    waited++;
                    yield return null;
                }

                InGameAssert.IsTrue(GuiTreeRecorder.LastCaptureWritten,
                    "the recorder never completed a capture within " + waited
                    + " frames after arming; armed=" + GuiTreeRecorder.ArmedFlag
                    + " pendingFlush=" + GuiTreeRecorder.HasPendingFlush);

                path = GuiTreeRecorder.LastWrittenPath ?? path;
                InGameAssert.IsTrue(File.Exists(path), "no dump file at " + path);

                string json = File.ReadAllText(path);
                ParsekLog.Info("TestRunner",
                    "GuiTree_InGame: dump bytes=" + json.Length + " path=" + path);

                // ---- the capture describes A window, and it is the probe's --------
                InGameAssert.Contains(json, "\"schema\": \"" + GuiTreeJson.SchemaId + "\"",
                    "dump is missing its schema marker");
                InGameAssert.Contains(json, "\"kind\": \"window\"",
                    "dump contains no window node, so GUI.CallWindowDelegate was not intercepted");
                InGameAssert.Contains(json, GuiTreeProbe.WindowTitle,
                    "dump does not carry the probe window's title");

                // ---- every control kind the probe drew ---------------------------
                InGameAssert.Contains(json, GuiTreeProbe.LabelText,
                    "the probe's GUILayout.Label is missing, so GUI.DoLabel was not intercepted");
                InGameAssert.Contains(json, GuiTreeProbe.ButtonText,
                    "the probe's GUILayout.Button is missing, so neither GUI.DoButton nor "
                    + "GUI.DoControl was intercepted");
                InGameAssert.Contains(json, GuiTreeProbe.ToggleText,
                    "the probe's GUILayout.Toggle is missing");
                InGameAssert.Contains(json, GuiTreeProbe.TextFieldValue,
                    "the probe's GUILayout.TextField content is missing");
                InGameAssert.Contains(json, GuiTreeProbe.ScrollRowText(0),
                    "the probe's first scroll-view row is missing");
                InGameAssert.Contains(json, GuiTreeProbe.ScrollRowText(2),
                    "the probe's third scroll-view row is missing");
                InGameAssert.Contains(json, "\"kind\": \"scrollview\"",
                    "no scroll view node, so GUI.BeginScrollView was not intercepted");
                InGameAssert.Contains(json, "\"kind\": \"layoutgroup\"",
                    "no layout group node, so GUILayout.BeginVertical was not intercepted");
                InGameAssert.Contains(json, "\"kind\": \"toggle\"",
                    "the toggle was captured but not classified as a toggle, so the "
                    + "DoToggle -> DoControl kind hint did not survive");
                InGameAssert.Contains(json, "\"kind\": \"textfield\"",
                    "no textfield node, so GUI.DoTextField was not intercepted");
                InGameAssert.Contains(json, "\"tooltip\": \"" + GuiTreeProbe.ButtonTooltip + "\"",
                    "the button's tooltip did not reach the dump");

                // ---- the tooltip / value / enabled surfaces ----------------------
                InGameAssert.Contains(json, "\"value\": true",
                    "the toggle's ON value did not reach the dump");
                InGameAssert.Contains(json, "\"enabled\": false",
                    "the deliberately disabled label did not record GUI.enabled=false");

                // ---- structural health: the assembler needed no guessing ---------
                InGameAssert.Contains(json, "\"strayEnds\": 0",
                    "the event stream contained End records with no matching Begin");
                InGameAssert.Contains(json, "\"recordFaults\": 0",
                    "a patch body faulted during the capture");
                InGameAssert.Contains(json, "\"droppedOverCap\": 0",
                    "the capture hit its per-frame event cap");

                // ---- geometry: children sit inside the window --------------------
                GuiTreeGeometryReport geometry = GuiTreeGeometry.Inspect(
                    GuiTreeRecorder.LastTree, GuiTreeProbe.WindowTitle, "parsek-probe-");
                InGameAssert.IsTrue(geometry.WindowFound,
                    "could not locate the probe window's rect in the dump");
                InGameAssert.IsTrue(geometry.ProbeControlsFound >= 6,
                    "expected at least 6 of the probe's controls in the dump, found "
                    + geometry.ProbeControlsFound);
                InGameAssert.AreEqual(0, geometry.ControlsOutsideWindow,
                    "screen rects of " + geometry.ControlsOutsideWindow + " probe control(s) fell "
                    + "outside the window rect " + geometry.DescribeWindowRect()
                    + ", so GUIUtility.GUIToScreenRect does not convert as assumed inside a "
                    + "GUI.Window callback. First offender: " + geometry.FirstOffender);
                InGameAssert.AreEqual(0, geometry.ControlsWithDegenerateRect,
                    "probe control(s) recorded a zero-size screen rect");
                InGameAssert.IsTrue(geometry.MaxDepth >= 3,
                    "expected nesting at least window > layoutgroup > control, saw depth "
                    + geometry.MaxDepth);

                ParsekLog.Info("TestRunner",
                    "GuiTree_InGame: PASS windowRect=" + geometry.DescribeWindowRect()
                    + " probeControls=" + geometry.ProbeControlsFound
                    + " maxDepth=" + geometry.MaxDepth
                    + " repaintPasses=" + probe.RepaintPasses
                    + " path=" + path);
            }
            finally
            {
                GuiTreeRecorder.Disarm("in-game test teardown");
                UnityEngine.Object.Destroy(go);
                // The dump is evidence: leave it in Screenshots/ for collect-logs.py.
            }
        }

        /// <summary>
        /// The probe window. Draws a fixed, known control tree every pass so the capture
        /// can be asserted against a shape decided here rather than against whatever
        /// Parsek's real windows happened to show.
        /// </summary>
        private sealed class GuiTreeProbe : MonoBehaviour
        {
            internal const int WindowId = 907311;
            internal const string WindowTitle = "Parsek GuiTree Probe";
            internal const string LabelText = "parsek-probe-label";
            internal const string DisabledLabelText = "parsek-probe-disabled-label";
            internal const string ButtonText = "parsek-probe-button";
            internal const string ButtonTooltip = "parsek probe button tooltip";
            internal const string ToggleText = "parsek-probe-toggle";
            internal const string TextFieldValue = "parsek-probe-text";
            internal const int ScrollRows = 3;

            internal static string ScrollRowText(int index)
            {
                return "parsek-probe-scroll-" + index;
            }

            internal int RepaintPasses;
            internal bool Faulted;
            internal string FaultMessage = string.Empty;

            private Rect windowRect = new Rect(60f, 60f, 320f, 260f);
            private Vector2 scroll = Vector2.zero;

            private void OnGUI()
            {
                EventType evt = Event.current.type;
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;
                try
                {
                    // Plain GUILayout.Window rather than ClickThruBlocker: the probe must
                    // not depend on a third-party assembly, and ClickThruBlocker's wrapper
                    // funnels into exactly this call anyway.
                    windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, WindowTitle);
                    if (evt == EventType.Repaint)
                        RepaintPasses++;
                }
                catch (Exception ex)
                {
                    Faulted = true;
                    FaultMessage = ex.GetType().Name + ": " + ex.Message;
                }
            }

            private void DrawWindow(int id)
            {
                GUILayout.BeginVertical();
                GUILayout.Label(LabelText);

                bool previousEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Label(DisabledLabelText);
                GUI.enabled = previousEnabled;

                GUILayout.Button(new GUIContent(ButtonText, ButtonTooltip));
                // Toggle held ON so the dump's "value": true is a real reading.
                GUILayout.Toggle(true, ToggleText);
                GUILayout.TextField(TextFieldValue);

                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(70f));
                for (int i = 0; i < ScrollRows; i++)
                    GUILayout.Label(ScrollRowText(i));
                GUILayout.EndScrollView();

                GUILayout.EndVertical();
                GUI.DragWindow();
            }
        }
    }
}
