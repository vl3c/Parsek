using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI guards for the UI-scale layer (<see cref="ParsekUiScale"/>, todo S10):
    /// what only a real Unity IMGUI pass can answer. (1) A window drawn through
    /// <see cref="ParsekUiScale.Window"/> inside a scaled host scope really lands at
    /// scale-times its rect on screen, the IMGUI mouse is mapped back through the matrix,
    /// and <see cref="ParsekUiScale.GuiPointToScaledRoot"/> undoes
    /// <c>GUIUtility.GUIToScreenPoint</c> (whose matrix handling the popup anchors rely
    /// on). (2) The scaled path's own input lock follows the window as DRAWN: the real OS
    /// pointer is parked over a spot that is inside the drawn window but outside its
    /// unscaled rect (exactly where ClickThroughBlocker's raw hit test would miss it), and
    /// the lock must be held there, released off the window, and swept once the window
    /// stops drawing. (3) The live read of <c>GameSettings.UI_SCALE</c>.
    ///
    /// <para>The probe draws at a FIXED scale through the explicit-scale host overload,
    /// so the cells measure the same thing on any install and never move the player's
    /// own windows.</para>
    /// </summary>
    public sealed class UiScaleImguiTest
    {
        internal const float ProbeScale = 1.5f;
        private const string LogTag = "TestRunner";

        [InGameTest(Category = "UiScale",
            Description = "A window drawn in a 1.5x UI-scale scope lands at 1.5x its rect on screen, the IMGUI mouse maps back through the matrix, and the popup-anchor conversion undoes GUIToScreenPoint")]
        public IEnumerator ScaledWindow_DrawsAtTheScale_AndTheConversionsAgree()
        {
            var go = new GameObject("ParsekUiScaleProbe");
            Object.DontDestroyOnLoad(go);
            UiScaleProbe probe = go.AddComponent<UiScaleProbe>();
            try
            {
                int frames = 0;
                while (probe.CallbackRepaints < 3 && frames < 240)
                {
                    frames++;
                    yield return null;
                }
                if (probe.CallbackRepaints == 0)
                {
                    InGameAssert.Skip("probe window never painted (frames=" + frames
                        + "); cannot measure the scaled draw in this context");
                    yield break;
                }
                InGameAssert.IsFalse(probe.Faulted, "probe threw while drawing: " + probe.FaultMessage);

                Rect r = probe.ResolvedRect;
                ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                    "UiScale_InGame: geometry rect=({0:0.#},{1:0.#},{2:0.#},{3:0.#}) callbackMatrixM00={4:0.###}"
                    + " screenOrigin=({5:0.#},{6:0.#}) scaledRootOrigin=({7:0.#},{8:0.#})"
                    + " rootMouse=({9:0.#},{10:0.#}) inputMouse=({11:0.#},{12:0.#}) screen={13}x{14}",
                    r.x, r.y, r.width, r.height, probe.CallbackMatrixM00,
                    probe.CallbackScreenOrigin.x, probe.CallbackScreenOrigin.y,
                    probe.CallbackScaledRootOrigin.x, probe.CallbackScaledRootOrigin.y,
                    probe.RootMouse.x, probe.RootMouse.y, probe.InputMouse.x, probe.InputMouse.y,
                    Screen.width, Screen.height));

                // The window callback runs under the scope's matrix: what it draws is scaled.
                InGameAssert.ApproxEqual(ProbeScale, probe.CallbackMatrixM00, 0.001f,
                    "GUI.matrix inside the window callback is not the scope's scale, so the window content would draw unscaled");

                // GUIToScreenPoint of the window's own origin is the PHYSICAL origin.
                InGameAssert.ApproxEqual(r.x * ProbeScale, probe.CallbackScreenOrigin.x, 1f,
                    "GUIToScreenPoint(window origin).x is not scale-times the rect x; the window is not where the scope put it");
                InGameAssert.ApproxEqual(r.y * ProbeScale, probe.CallbackScreenOrigin.y, 1f,
                    "GUIToScreenPoint(window origin).y is not scale-times the rect y");

                // ... and the anchor conversion brings it back to the rect's own units, so
                // a popup opened at a button lands on that button.
                InGameAssert.ApproxEqual(r.x, probe.CallbackScaledRootOrigin.x, 1f,
                    "GuiPointToScaledRoot(window origin).x is not the rect x; popups would open off their button");
                InGameAssert.ApproxEqual(r.y, probe.CallbackScaledRootOrigin.y, 1f,
                    "GuiPointToScaledRoot(window origin).y is not the rect y");

                // The IMGUI mouse at the scope root is the physical pointer over the scale.
                // Input.mousePosition is y-up physical pixels.
                float expectX = probe.InputMouse.x / ProbeScale;
                float expectY = (Screen.height - probe.InputMouse.y) / ProbeScale;
                InGameAssert.ApproxEqual(expectX, probe.RootMouse.x, 1.5f,
                    "IMGUI mouse x inside the scope is not the physical pointer divided by the scale");
                InGameAssert.ApproxEqual(expectY, probe.RootMouse.y, 1.5f,
                    "IMGUI mouse y inside the scope is not the physical pointer divided by the scale");

                ParsekLog.Info(LogTag, "UiScale_InGame: PASS geometry scale="
                    + ProbeScale.ToString("0.###", CultureInfo.InvariantCulture));
            }
            finally
            {
                Object.Destroy(go);
                ParsekUiScale.SweepStaleLocks();
            }
        }

        [InGameTest(Category = "UiScale",
            Description = "Under a UI scale the window's input lock follows the window as drawn: held where only the scaled window is, released off it, swept when the window stops drawing")]
        public IEnumerator ScaledWindow_InputLockFollowsTheDrawnWindow()
        {
            var go = new GameObject("ParsekUiScaleLockProbe");
            Object.DontDestroyOnLoad(go);
            UiScaleProbe probe = go.AddComponent<UiScaleProbe>();
            DisabledHoverEchoImguiTest.CursorPlacement first = default(DisabledHoverEchoImguiTest.CursorPlacement);
            bool havePlacement = false;
            bool probeAlive = true;
            try
            {
                int frames = 0;
                while (probe.CallbackRepaints < 3 && frames < 240)
                {
                    frames++;
                    yield return null;
                }
                if (probe.CallbackRepaints == 0)
                {
                    InGameAssert.Skip("probe window never painted; cannot measure the scaled lock in this context");
                    yield break;
                }
                InGameAssert.IsFalse(probe.Faulted, "probe threw while drawing: " + probe.FaultMessage);

                Rect r = probe.ResolvedRect;
                // Inside the DRAWN window (physical = rect * scale), near its bottom-right
                // corner, which is past the unscaled rect's right edge.
                int insideX = Mathf.RoundToInt(r.xMax * ProbeScale - 20f);
                int insideY = Mathf.RoundToInt(r.yMax * ProbeScale - 20f);
                InGameAssert.IsTrue(insideX > r.xMax && insideY > r.yMax,
                    "probe geometry no longer separates the drawn window from its unscaled rect");
                int outsideX = Mathf.RoundToInt(r.xMax * ProbeScale + 60f);
                int outsideY = Mathf.RoundToInt(r.yMax * ProbeScale + 60f);
                if (outsideX >= Screen.width || outsideY >= Screen.height)
                {
                    InGameAssert.Skip("screen " + Screen.width + "x" + Screen.height
                        + " too small for an off-window pointer spot");
                    yield break;
                }

                first = DisabledHoverEchoImguiTest.CursorPlacement.TryPlaceAtClientPoint(insideX, insideY);
                havePlacement = first.Placed;
                ParsekLog.Info(LogTag, "UiScale_InGame: cursor placement inside " + first.Describe());
                if (!first.Placed)
                {
                    InGameAssert.Skip("cannot park the OS pointer here (" + first.Describe()
                        + "); the lock cannot be measured without a real hover");
                    yield break;
                }

                bool heldInside = false;
                for (int i = 0; i < 30 && !heldInside; i++)
                {
                    yield return null;
                    heldInside = ParsekUiScale.IsScaledLockHeld(UiScaleProbe.WindowId)
                        && InputLockManager.GetControlLock(ParsekUiScale.ScaledLockId(UiScaleProbe.WindowId))
                            != ControlTypes.None;
                }
                ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                    "UiScale_InGame: lock inside pointer=({0},{1}) rootMouse=({2:0.#},{3:0.#}) rect=({4:0.#},{5:0.#},{6:0.#},{7:0.#}) held={8}",
                    insideX, insideY, probe.RootMouse.x, probe.RootMouse.y,
                    r.x, r.y, r.width, r.height, heldInside));
                if (!probe.RootMouseInsideRect && !heldInside)
                {
                    InGameAssert.Skip("pointer placement did not reach the game (root mouse "
                        + probe.RootMouse + " not over the probe); nothing to conclude");
                    yield break;
                }
                InGameAssert.IsTrue(heldInside,
                    "pointer over the scaled window (outside its unscaled rect) but the Parsek scaled-window lock is not held; clicks there would fall through to the game");

                // No flapping: the probe hands a zero-height rect every frame, as the main
                // window does, so a lock hit test on the Layout pass would release and
                // re-take the lock every frame.
                int transitionsHeld = ParsekUiScale.LockTransitionCount;
                for (int i = 0; i < 20; i++)
                    yield return null;
                int flaps = ParsekUiScale.LockTransitionCount - transitionsHeld;
                ParsekLog.Info(LogTag, "UiScale_InGame: lock transitions over 20 hovered frames="
                    + flaps.ToString(CultureInfo.InvariantCulture));
                InGameAssert.AreEqual(0, flaps,
                    "the scaled-window lock flapped while the pointer rested on a content-sized window");
                InGameAssert.IsTrue(ParsekUiScale.IsScaledLockHeld(UiScaleProbe.WindowId),
                    "the scaled-window lock was lost while the pointer rested on the window");

                var off = DisabledHoverEchoImguiTest.CursorPlacement.TryPlaceAtClientPoint(outsideX, outsideY);
                InGameAssert.IsTrue(off.Placed, "could not move the pointer off the window: " + off.Describe());
                bool releasedOff = false;
                for (int i = 0; i < 30 && !releasedOff; i++)
                {
                    yield return null;
                    releasedOff = !ParsekUiScale.IsScaledLockHeld(UiScaleProbe.WindowId);
                }
                InGameAssert.IsTrue(releasedOff,
                    "pointer left the scaled window but the scaled-window lock is still held; the game would stay input-locked");

                // Hover again, then stop drawing: the sweep must free the lock.
                DisabledHoverEchoImguiTest.CursorPlacement.TryPlaceAtClientPoint(insideX, insideY);
                bool heldAgain = false;
                for (int i = 0; i < 30 && !heldAgain; i++)
                {
                    yield return null;
                    heldAgain = ParsekUiScale.IsScaledLockHeld(UiScaleProbe.WindowId);
                }
                InGameAssert.IsTrue(heldAgain, "scaled-window lock not re-taken on the second hover");
                Object.Destroy(go);
                probeAlive = false;
                yield return null;
                yield return null;
                yield return null;
                ParsekUiScale.SweepStaleLocks();
                InGameAssert.IsFalse(ParsekUiScale.IsScaledLockHeld(UiScaleProbe.WindowId),
                    "the probe window stopped drawing but its scaled-window lock survived the sweep");
                InGameAssert.AreEqual(ControlTypes.None,
                    InputLockManager.GetControlLock(ParsekUiScale.ScaledLockId(UiScaleProbe.WindowId)),
                    "InputLockManager still carries the swept scaled-window lock");
                ParsekLog.Info(LogTag, "UiScale_InGame: PASS lock follows the drawn window");
            }
            finally
            {
                if (havePlacement) first.Restore();
                if (probeAlive) Object.Destroy(go);
                ParsekUiScale.SweepStaleLocks();
            }
        }

        [InGameTest(Category = "UiScale",
            Description = "Parsek's UI scale is the live stock UI Scale setting, clamped and snapped")]
        public void TheScaleIsTheLiveStockSetting()
        {
            float raw = GameSettings.UI_SCALE;
            float applied = ParsekUiScale.CurrentSettingScale();
            ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                "UiScale_InGame: setting UI_SCALE={0:0.###} applied={1:0.###} override={2}",
                raw, applied, ParsekUiScale.OverrideForTesting.HasValue ? "set" : "none"));
            InGameAssert.IsFalse(ParsekUiScale.OverrideForTesting.HasValue,
                "the UI-scale test override is set outside a test; players would not get their own scale");
            InGameAssert.ApproxEqual(ParsekUiScale.ResolveScale(raw), applied, 0.0001f,
                "Parsek's applied scale is not the stock UI_SCALE setting");
        }

        /// <summary>Draws one window through <see cref="ParsekUiScale.Window"/> inside a
        /// fixed-scale host scope and records what the IMGUI pass saw.</summary>
        private sealed class UiScaleProbe : MonoBehaviour
        {
            internal const int WindowId = 0x5A1E5CA;

            internal int CallbackRepaints;
            internal bool Faulted;
            internal string FaultMessage;
            internal Rect DrawnRect = new Rect(200f, 100f, 300f, 200f);
            // The last Repaint's resolved rect; DrawnRect itself is height-zeroed per draw.
            internal Rect ResolvedRect;
            internal float CallbackMatrixM00;
            internal Vector2 CallbackScreenOrigin;
            internal Vector2 CallbackScaledRootOrigin;
            internal Vector2 RootMouse;
            internal Vector3 InputMouse;
            internal bool RootMouseInsideRect;

            private void OnGUI()
            {
                try
                {
                    using (ParsekUiScale.BeginHost("UiScaleProbe", ProbeScale))
                    {
                        // Content-sized like the main window: the height is zeroed before
                        // every draw, so the Layout pass returns a zero-height rect.
                        DrawnRect.height = 0f;
                        Rect drawn = ParsekUiScale.Window(WindowId, DrawnRect, DrawProbe,
                            "Parsek UI scale probe", GUI.skin.window,
                            GUILayout.Width(300f));
                        if (Event.current.type == EventType.Repaint)
                        {
                            DrawnRect = drawn;
                            ResolvedRect = drawn;
                            RootMouse = Event.current.mousePosition;
                            InputMouse = Input.mousePosition;
                            RootMouseInsideRect = drawn.Contains(RootMouse);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Faulted = true;
                    FaultMessage = ex.GetType().Name + ": " + ex.Message;
                }
            }

            private void DrawProbe(int id)
            {
                GUILayout.Label("Parsek UI scale probe");
                GUILayout.Space(170f);
                if (Event.current.type == EventType.Repaint)
                {
                    CallbackMatrixM00 = GUI.matrix.m00;
                    CallbackScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
                    CallbackScaledRootOrigin = ParsekUiScale.GuiPointToScaledRoot(Vector2.zero);
                    CallbackRepaints++;
                }
            }
        }
    }
}
