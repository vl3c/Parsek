using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI proof that <see cref="GuiTreeRecorder"/> captures a REAL Repaint pass:
    /// a probe MonoBehaviour draws a window of known shape, the recorder is armed for one
    /// frame, and the captured tree is checked against that shape - the right controls,
    /// the right nesting, screen rects inside the window's own measured content box, and
    /// a scroll offset that survived the conversion.
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
    /// <para><b>What is scoped and what is not.</b> A capture is process-wide: every
    /// window drawn during the armed frame is in it, and during a test batch the Test
    /// Runner window - which draws a text field, a scroll view and disabled controls of
    /// its own - is one of them. So the EXACT expectations here are stated over the probe
    /// window's own subtree (<c>GuiTreeGeometry.Inspect</c>'s per-kind counts), and the
    /// process-wide numbers (funnel hits, the assembler's repair counters) are asserted
    /// only where they are shape-independent: Begin/End PARITY, which no window can break
    /// without one of its funnels having been bypassed.</para>
    ///
    /// <para><b>Never flown.</b> The spike's author cannot launch KSP. Every exact pin
    /// below is a prediction from the decompiled IMGUI source, and each failure message
    /// prints the measured counts so the first flight can correct the pin rather than
    /// guess at it.</para>
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

        /// <summary>
        /// Slack on the scroll-offset reading, and the derivation behind the pin it
        /// tolerates.
        ///
        /// <para><b>The derivation.</b> Write <c>W</c> for the window's content origin in
        /// screen space and <c>P</c> for the scroll view's own rect in the space it is
        /// declared in. Decompiled, <c>GUILayout.BeginScrollView</c> calls
        /// <c>GUI.BeginScrollView(group.rect, scrollPosition, new Rect(0, 0, clientWidth,
        /// clientHeight), ...)</c>, so <b><c>viewRect.y</c> is 0</b> and the clip
        /// <c>GUI.BeginScrollView</c> pushes carries
        /// <c>scrollOffset.y = round(-scroll.y - viewRect.y) = -round(scroll.y)</c>.</para>
        ///
        /// <para>The scroll view's node rect is converted BEFORE that push, so
        /// <c>scrollView.rect.y = W.y + P.y</c>. Row 0 is drawn AFTER it, so
        /// <c>row0.rect.y = W.y + P.y - scroll.y + m</c>, where <c>m</c> is the row's own
        /// top margin inside the scroll content (a couple of px of stock skin). Hence
        /// <c>offsetAbove = scrollView.rect.y - row0.rect.y = scroll.y - m</c>: exactly
        /// <see cref="GuiTreeProbe.ScrollOffsetPx"/> minus a small margin, which is what
        /// this slack is for.</para>
        ///
        /// <para><b>What a failure means.</b> Zero means the scroll offset never reached
        /// the screen conversion at all. A reading near <c>P.y</c> - roughly 130 px in
        /// this window, far past the slack - means the scroll view's own rect was
        /// converted under its OWN clip and therefore carries its origin and its scroll
        /// offset twice, which is the defect the <c>BeginScrollView</c> prefix seam
        /// exists to prevent.</para>
        /// </summary>
        private const float ScrollOffsetSlackPx = 10f;

        /// <summary>
        /// Probe controls whose text carries the containment marker: the two labels, the
        /// button, the toggle and the text field. The 12 scroll rows deliberately do NOT
        /// (see <see cref="GuiTreeProbe.ScrollRowText"/>) - a row scrolled out of view is
        /// LEGITIMATELY outside the window's content box, because clipped children are
        /// recorded rather than culled, so marking them would make the containment check
        /// fail on correct behaviour.
        /// </summary>
        private const int ExpectedMarkedControls = 5;

        [InGameTest(Category = "GuiTree",
            Description = "Arming the GUI tree recorder for one Repaint captures a probe window's real control tree - window, vertical group, 2 labels, button, toggle, text field and a 12-row scroll view - with exact per-kind counts inside the window, no assembler repairs, Begin/End funnel parity, screen rects inside the window's measured content box, and a scroll offset that survived the screen conversion")]
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
                InGameAssert.IsNotNull(path,
                    "ArmForNextRepaint returned no output path; it refuses to arm from "
                    + "inside an IMGUI pass, and a test coroutine is not one");

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
                InGameAssert.Contains(json, "\"schema\": \"" + GuiTreeJson.SchemaId + "\"",
                    "dump is missing its schema marker");

                GuiTreeResult tree = GuiTreeRecorder.LastTree;
                InGameAssert.IsNotNull(tree, "the recorder kept no assembled tree");

                // ---- the probe's own subtree, EXACTLY ----------------------------
                // Everything here is scoped to the probe window, so another window on
                // screen cannot move a number.
                // Keyed on the WINDOW ID, with the title as the secondary key: the title
                // is written only by GUI.DoWindow (26 bytes, inline-prone) while the node
                // itself comes from CallWindowDelegate, which cannot be inlined and whose
                // fallback node carries the id and no title.
                GuiTreeGeometryReport geometry = GuiTreeGeometry.Inspect(
                    tree, GuiTreeProbe.WindowId, GuiTreeProbe.WindowTitle, "parsek-probe-");
                string counts = geometry.DescribeKindCounts();
                InGameAssert.IsTrue(geometry.WindowFound, DescribeWindowMiss());

                AssertKind(geometry, GuiNodeKind.Label, GuiTreeProbe.ExpectedLabels,
                    "GUI.DoLabel", counts);
                AssertKind(geometry, GuiNodeKind.Button, 1, "GUI.DoButton / GUI.DoControl", counts);
                AssertKind(geometry, GuiNodeKind.Toggle, 1,
                    "GUI.DoToggle's kind hint (a toggle recorded as 'control' means the "
                    + "hint did not survive)", counts);
                AssertKind(geometry, GuiNodeKind.TextField, 1, "GUI.DoTextField", counts);
                AssertKind(geometry, GuiNodeKind.ScrollView, 1, "GUI.BeginScrollView", counts);
                AssertKind(geometry, GuiNodeKind.LayoutGroup, 1,
                    "GUILayoutUtility.BeginLayoutGroup (exactly ONE: the probe's "
                    + "BeginVertical. A second one means the scroll view's GUIScrollGroup "
                    + "carrier was recorded as a group too)", counts);
                AssertKind(geometry, GuiNodeKind.Box, 0,
                    "GUI.Box (the probe styles no group, so nothing should draw one)", counts);
                InGameAssert.IsTrue(geometry.CountOf(GuiNodeKind.Slider) >= 1,
                    "the scroll view drew no scrollbar, so GUI.Slider was bypassed or the "
                    + "12 rows fitted the 70 px viewport after all; counts: " + counts);
                InGameAssert.IsTrue(geometry.CountOf(GuiNodeKind.RepeatButton) >= 2,
                    "the scrollbar's two arrow buttons are missing, so GUI.DoRepeatButton "
                    + "was bypassed; counts: " + counts);

                // ---- the surfaces that are not a count --------------------------
                InGameAssert.Contains(json, GuiTreeProbe.ButtonTooltip,
                    "the button's tooltip did not reach the dump");
                InGameAssert.Contains(json, "\"value\": true",
                    "the toggle's ON value did not reach the dump");
                InGameAssert.Contains(json, "\"enabled\": false",
                    "the deliberately disabled label did not record GUI.enabled=false");
                InGameAssert.Contains(json, GuiTreeProbe.TextFieldValue,
                    "the text field's content did not reach the dump");

                // ---- the assembler needed no guessing ---------------------------
                // Whole-capture counters: a non-zero reading may name ANOTHER window in
                // the frame, and that is still the finding this cell exists to make - it
                // means some End funnel was bypassed and the tree was repaired rather
                // than recorded.
                AssertRepair(tree.StrayEnds, "strayEnds",
                    "End records that matched no open container", tree);
                AssertRepair(tree.AutoClosedByEnd, "autoClosedByEnd",
                    "containers closed by an OUTER container's End", tree);
                // autoClosedByClip is NOT asserted zero, and neither is
                // GUI.BeginGroup/GUI.EndGroup parity: GUI.EndGroup is 14 bytes of IL and
                // is EXPECTED to be inlined, which is precisely what the clip-depth rule
                // is the designed fallback for. Asserting either at zero would red on the
                // fallback WORKING. Both are read out on the PASS line below, and the
                // dump's own `funnels` block carries the measurement.
                AssertRepair(tree.AutoClosedByRect, "autoClosedByRect",
                    "layout groups closed by rect containment "
                    + "(GUILayoutUtility.EndLayoutGroup inlined)", tree);
                AssertRepair(tree.UnclosedAtEnd, "unclosedAtEnd",
                    "containers still open when the frame ended", tree);
                InGameAssert.AreEqual(0, GuiTreeRecorder.LastRecordFaults,
                    "a patch body faulted during the capture");
                InGameAssert.AreEqual(0, GuiTreeRecorder.LastDroppedOverCap,
                    "the capture hit its per-frame event cap");

                // ---- Begin/End funnel parity: the inlining detector --------------
                // Process-wide but shape-independent: every Begin in a well-formed GUI
                // pass has its End, so an inequality means one side was bypassed.
                //
                // Only the pairs whose BOTH sides are too large for Mono to inline are
                // asserted: GUILayoutUtility.BeginLayoutGroup / EndLayoutGroup (180 and
                // 123 bytes of IL) and GUI.BeginScrollView / EndScrollView (1199 and
                // 362). GUI.BeginGroup / GUI.EndGroup is NOT one of them - the End is 14
                // bytes and the design EXPECTS it inlined - so its reading goes on the
                // PASS line instead of into an assertion that would red on the clip-depth
                // fallback doing its job.
                AssertParity(GuiFunnel.BeginLayoutGroup, GuiFunnel.EndLayoutGroup);
                AssertParity(GuiFunnel.BeginScrollView, GuiFunnel.EndScrollView);
                InGameAssert.IsTrue(Hits(GuiFunnel.CallWindowDelegate) >= 1,
                    "GUI.CallWindowDelegate recorded no hit, so no window body ran through "
                    + "the interception at all");
                InGameAssert.IsTrue(Hits(GuiFunnel.DoLabel) >= GuiTreeProbe.ExpectedLabels,
                    "GUI.DoLabel recorded " + Hits(GuiFunnel.DoLabel) + " hits, fewer than "
                    + "the probe's own " + GuiTreeProbe.ExpectedLabels + " labels");

                // ---- geometry: children sit inside the window's measured box -----
                InGameAssert.AreEqual(ExpectedMarkedControls, geometry.ProbeControlsFound,
                    "expected exactly " + ExpectedMarkedControls + " marked probe controls "
                    + "(2 labels, button, toggle, text field - the 12 scroll rows carry a "
                    + "DIFFERENT marker on purpose, because a row scrolled out of view is "
                    + "legitimately outside the window box), found "
                    + geometry.ProbeControlsFound + "; counts: " + counts);
                InGameAssert.IsTrue(geometry.ContentBoxMeasured,
                    "the window node carries no contentOrigin / argSize, so the containment "
                    + "test fell back to the DECLARED rect - GUI.CallWindowDelegate's "
                    + "prefix did not measure the window");
                InGameAssert.AreEqual(0, geometry.ControlsOutsideWindow,
                    "screen rects of " + geometry.ControlsOutsideWindow + " probe control(s) fell "
                    + "outside the window's measured content box "
                    + geometry.DescribeContainmentBox()
                    + " (declared rect " + geometry.DescribeWindowRect() + "), so "
                    + "GUIUtility.GUIToScreenRect does not convert as assumed inside a "
                    + "GUI.Window callback. First offender: " + geometry.FirstOffender);
                InGameAssert.AreEqual(0, geometry.ControlsWithDegenerateRect,
                    "probe control(s) recorded a zero-size screen rect: " + geometry.FirstOffender);
                InGameAssert.IsTrue(geometry.MaxDepth >= 4,
                    "expected nesting at least window > layoutgroup > scrollview > label, "
                    + "saw depth " + geometry.MaxDepth);
                InGameAssert.IsFalse(GuiTreeRecorder.LastMatrixNonIdentity,
                    "GUI.matrix was NOT identity during the capture; the recorded sizes "
                    + "were scaled through by hand and this cell's expectations do not "
                    + "model that");

                // ---- the scroll offset: the ONLY reading that exercises the clip's
                // scroll offset reaching the screen conversion --------------------
                GuiTreeScrollOffsetReport scroll = GuiTreeGeometry.MeasureScrollOffset(
                    tree, GuiTreeProbe.WindowId, GuiTreeProbe.WindowTitle,
                    GuiTreeProbe.ScrollRowText(0));
                InGameAssert.IsTrue(scroll.RowFound,
                    "the scroll view's first row is not under the scroll view node, so the "
                    + "scroll view held none of its rows: " + scroll.Describe());
                // Derivation and the two failure readings: see ScrollOffsetSlackPx. In
                // short, viewRect.y is 0 for a GUILayout scroll view, so the expected
                // reading is scroll.y minus the row's own top margin; 0 means the offset
                // never reached the conversion and about 130 means the view's rect was
                // converted under its own clip.
                InGameAssert.ApproxEqual(GuiTreeProbe.ScrollOffsetPx, scroll.OffsetAbovePx,
                    ScrollOffsetSlackPx,
                    "the first scroll row should sit about " + GuiTreeProbe.ScrollOffsetPx
                    + " px ABOVE the scroll view's top (scroll.y minus the row's top "
                    + "margin, because GUILayout passes viewRect.y=0). A reading of ~0 "
                    + "means the scroll offset the scroll view pushes onto the clip stack "
                    + "did not reach the screen conversion; a reading of roughly the "
                    + "view's own window-local y means the view's rect was converted "
                    + "under its OWN clip and counts its origin and offset twice: "
                    + scroll.Describe());

                ParsekLog.Info("TestRunner",
                    "GuiTree_InGame: PASS box=" + geometry.DescribeContainmentBox()
                    + " declared=" + geometry.DescribeWindowRect()
                    + " kinds=" + counts
                    + " maxDepth=" + geometry.MaxDepth
                    + " " + scroll.Describe()
                    // Readings, not assertions: GUI.EndGroup is 14 bytes of IL and the
                    // clip-depth rule is its designed fallback, so a bypass here is
                    // expected rather than a failure. autoClosedByClip is how much
                    // repairing that fallback had to do.
                    + " beginGroupHits=" + Hits(GuiFunnel.BeginGroup)
                    + " endGroupHits=" + Hits(GuiFunnel.EndGroup)
                    + " autoClosedByClip=" + tree.AutoClosedByClip
                    + " rectRuleInert=" + tree.RectRuleInert
                    + " clipProbe=" + (GuiTreeRecorder.LastClipProbeBinding ?? "unread")
                    + " repaintPasses=" + probe.RepaintPasses
                    + " path=" + path);
            }
            finally
            {
                GuiTreeRecorder.Disarm("in-game test teardown");
                // Stops the probe drawing even if the Destroy below does not run.
                probe.Completed = true;
                UnityEngine.Object.Destroy(go);
                // The dump is evidence: leave it in Screenshots/ for collect-logs.py.
            }
        }

        private static int Hits(GuiFunnel funnel)
        {
            return GuiTreeFunnels.Hits[(int)funnel];
        }

        private static bool PatchedAtArm(GuiFunnel funnel)
        {
            return GuiTreeFunnels.PatchedAtArm[(int)funnel];
        }

        /// <summary>
        /// The "window not in the dump" message, SPLIT on the two funnels involved so the
        /// reading names the cause instead of blaming <c>CallWindowDelegate</c> for an
        /// inlined <c>DoWindow</c>. The lookup is keyed on the window id, so the node is
        /// findable even with no title on it - which means a miss here really is a missing
        /// NODE, and the node comes from <c>CallWindowDelegate</c> alone.
        /// </summary>
        private static string DescribeWindowMiss()
        {
            return "the probe window (id=" + GuiTreeProbe.WindowId + ", title \""
                + GuiTreeProbe.WindowTitle + "\") is not in the dump, keyed on the id and "
                + "then the title. GUI.CallWindowDelegate patchedAtArm="
                + PatchedAtArm(GuiFunnel.CallWindowDelegate)
                + " hits=" + Hits(GuiFunnel.CallWindowDelegate)
                + "; GUI.DoWindow patchedAtArm=" + PatchedAtArm(GuiFunnel.DoWindow)
                + " hits=" + Hits(GuiFunnel.DoWindow) + ". Read it this way: "
                + "CallWindowDelegate hits=0 means no window body ran through the "
                + "interception at all (it is [RequiredByNativeCode] and cannot be "
                + "inlined, so suspect the patch, not Mono); CallWindowDelegate hits>0 "
                + "with DoWindow hits=0 means the 26-byte declaration funnel WAS inlined, "
                + "so the node exists but carries no title - and finding it is then "
                + "exactly what the id key is for, so a miss with that pattern is a "
                + "window-id mismatch, not an inlining problem.";
        }

        private static void AssertParity(GuiFunnel begin, GuiFunnel end)
        {
            int b = Hits(begin);
            int e = Hits(end);
            InGameAssert.AreEqual(b, e,
                GuiTreeFunnels.Name(begin) + " was hit " + b + " times but "
                + GuiTreeFunnels.Name(end) + " " + e + " times. Every Begin in a "
                + "well-formed GUI pass has its End, so one of the two was BYPASSED - "
                + "Mono inlined it into its caller, or its signature drifted "
                + "(patchedAtArm begin=" + GuiTreeFunnels.PatchedAtArm[(int)begin]
                + " end=" + GuiTreeFunnels.PatchedAtArm[(int)end] + ")");
        }

        private static void AssertKind(GuiTreeGeometryReport geometry, GuiNodeKind kind,
            int expected, string funnel, string counts)
        {
            InGameAssert.AreEqual(expected, geometry.CountOf(kind),
                "expected " + expected.ToString(CultureInfo.InvariantCulture) + " "
                + GuiTreeAssembler.KindName(kind) + " node(s) inside the probe window, saw "
                + geometry.CountOf(kind).ToString(CultureInfo.InvariantCulture)
                + " - the funnel at stake is " + funnel
                + ". All kinds inside the window: " + counts);
        }

        private static void AssertRepair(int value, string name, string what, GuiTreeResult tree)
        {
            InGameAssert.AreEqual(0, value,
                name + "=" + value + ": " + what + ". This counter is whole-capture, so it "
                + "may name another window in the frame - either way an End funnel was "
                + "bypassed and the tree was repaired rather than recorded. Full repair "
                + "reading: strayEnds=" + tree.StrayEnds
                + " autoClosedByEnd=" + tree.AutoClosedByEnd
                + " autoClosedByClip=" + tree.AutoClosedByClip
                + " autoClosedByRect=" + tree.AutoClosedByRect
                + " rectRuleInert=" + tree.RectRuleInert
                + " unclosedAtEnd=" + tree.UnclosedAtEnd);
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

            /// <summary>
            /// Deliberately more rows than fit the viewport: the scroll view must SCROLL,
            /// or the offset reading below measures nothing.
            /// </summary>
            internal const int ScrollRows = 12;

            /// <summary>Viewport height, in px. 12 rows do not fit it.</summary>
            internal const float ScrollViewHeightPx = 70f;

            /// <summary>
            /// How far the scroll view is scrolled down, re-applied every frame so the
            /// reading is deterministic rather than "whatever Unity clamped it to last
            /// time". 12 rows of about 20 px against a 70 px viewport leave far more than
            /// this much to scroll, so Unity's own clamp cannot reduce it.
            /// </summary>
            internal const float ScrollOffsetPx = 25f;

            /// <summary>Two plain labels plus one per scroll row.</summary>
            internal const int ExpectedLabels = 2 + ScrollRows;

            /// <summary>
            /// Repaint passes after which the probe stops drawing by itself. An abandoned
            /// iterator - a cancelled batch, or an assertion that throws before the
            /// finally - never destroys the probe's GameObject, and without this the
            /// window would keep drawing over the game for the rest of the session. Well
            /// past the cell's own 240-frame settle plus 300-frame wait.
            /// </summary>
            internal const int MaxRepaintPasses = 1200;

            /// <summary>
            /// Row text, marked OUTSIDE the containment marker
            /// (<c>parsek-probe-</c>) on purpose. A row scrolled out of the viewport is
            /// clipped, not culled: the recorder still records its rect, which lies
            /// legitimately outside the window's content box. Marking these rows would
            /// make the geometry containment check fail on correct behaviour, so they
            /// carry their own prefix and the scroll-offset reading keys on the FULL row
            /// text instead.
            /// </summary>
            internal static string ScrollRowText(int index)
            {
                return "parsekscroll-" + index.ToString(CultureInfo.InvariantCulture);
            }

            internal int RepaintPasses;
            internal bool Faulted;
            internal string FaultMessage = string.Empty;

            /// <summary>
            /// Set by the cell's teardown, and by the probe itself once it has drawn far
            /// past any capture that could still be waiting on it. Stops OnGUI.
            /// </summary>
            internal bool Completed;

            private Rect windowRect = new Rect(60f, 60f, 320f, 300f);
            private Vector2 scroll = new Vector2(0f, ScrollOffsetPx);

            private void OnGUI()
            {
                // The same early-out DisabledHoverEchoImguiTest's probe has. An abandoned
                // iterator leaves this GameObject alive (it is DontDestroyOnLoad and the
                // cell's finally is what destroys it), and a probe that faulted once will
                // fault every pass, so both states stop drawing rather than papering the
                // game with a window nobody can close.
                if (Completed || Faulted)
                    return;

                // Inside OnGUI by construction, so Event.current is this pass's own event
                // and .type is the pass type. (The recorder's arm / unpatch guard cannot
                // use Event.current for the same reading - see IsInsideGuiPass - but a
                // .type test reached only from inside a pass is unaffected by that.)
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
                    {
                        RepaintPasses++;
                        if (RepaintPasses >= MaxRepaintPasses)
                            Completed = true;
                    }
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

                // Re-applied rather than carried: the assertion is about a KNOWN offset.
                scroll = new Vector2(0f, ScrollOffsetPx);
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(ScrollViewHeightPx));
                for (int i = 0; i < ScrollRows; i++)
                    GUILayout.Label(ScrollRowText(i));
                GUILayout.EndScrollView();

                GUILayout.EndVertical();
                GUI.DragWindow();
            }
        }
    }
}
