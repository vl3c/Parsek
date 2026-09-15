using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the Windows applier for <c>UiAction op=pointer</c>. Every
    /// decision - the arg shapes, the client-box gate, the y-flip and the read-back
    /// tolerance - lives in the pure sibling <see cref="TestCommandUiPointer"/>; this file
    /// owns the only two things that cannot: the <c>user32</c> calls and the
    /// <c>Input.mousePosition</c> read.
    ///
    /// <para>
    /// WHY P/INVOKE AT ALL. Unity exposes no way to WRITE the mouse position:
    /// <c>Input.mousePosition</c> is read-only and <c>Event.current.mousePosition</c> is
    /// filled by the engine from it. Four Parsek surfaces (the IMGUI hover style,
    /// <c>GUI.tooltip</c>, the <c>TooltipEchoBox</c> strip and <c>DisabledHoverEcho</c>)
    /// paint only when that value is inside a control's rect, so a census of them has to
    /// move the real cursor. <c>SetCursorPos</c> is the whole mechanism.
    /// </para>
    ///
    /// <para>
    /// THE THREE COORDINATE FRAMES. The wire is GAME CLIENT pixels, y DOWN - the frame the
    /// GUI-tree dump reports, so an <c>op=find</c> answer chains straight in.
    /// <c>SetCursorPos</c> takes DESKTOP pixels, y down, reached by adding the window's
    /// client origin through <c>ClientToScreen</c>. <c>Input.mousePosition</c> is client
    /// pixels, y UP, converted back by
    /// <c>TestCommandUiPointer.GuiYFromUnityMouseY</c> for the read-back. Getting any one
    /// of the three wrong lands the cursor somewhere plausible and photographs a hover of
    /// the wrong control, which is why the op confirms rather than assuming.
    /// </para>
    ///
    /// <para>
    /// HOW THE WINDOW IS FOUND, in order, with the answer reported as <c>via=</c>:
    /// <c>GetActiveWindow()</c> (the calling thread's active window - Unity's main thread
    /// owns the game window, so this is it whenever the game has focus, and
    /// <c>IntPtr.Zero</c> when it does not); then <c>FindWindow(null, productName)</c> and
    /// <c>FindWindow(null, "Kerbal Space Program")</c> by title; then, as a last resort, an
    /// assumed client origin of (0,0), which is exactly right for exclusive fullscreen and
    /// is safe to guess ONLY because the read-back catches it when it is not - a wrong
    /// origin lands the cursor off the control and answers
    /// <c>pointer-not-applied</c> rather than reporting a hover that never happened.
    /// <c>GetForegroundWindow()</c> is deliberately NOT in the chain: it answers with
    /// whatever window is foreground, including another process's, and there is nothing to
    /// check that against.
    /// </para>
    ///
    /// <para>
    /// THE FOCUS ASYMMETRY, checked in both directions. A foreground window belonging to
    /// ANOTHER process does not block the move: <c>SetCursorPos</c> is machine-wide, so the
    /// cursor lands over the game whether or not the game has focus. What focus governs is
    /// whether Unity UPDATES <c>Input.mousePosition</c> - an unfocused player may not - and
    /// that is the same condition that decides whether the hover would have painted. So the
    /// read-back is not a proxy for the move; it is the actual question, and an unfocused
    /// game is an honest ERROR instead of a capture of an unhovered control.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- user32 -----
        //
        // Resolved LAZILY by the CLR at the first call, which is what keeps this file inert
        // off Windows: the platform gate below returns before any of these is touched, so a
        // mono host never tries to bind user32 and the xUnit suite (which runs under mono on
        // the Linux CI) never loads them at all.

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point point);

        // ----- focus= -----
        //
        // THE DOCUMENTED RULE, and why two rungs. SetForegroundWindow succeeds only for a
        // process that already owns the foreground, was started by it, received the last
        // input event, or has AllowSetForegroundWindow granted to it; otherwise Windows
        // refuses and merely flashes the taskbar button. The game window belongs to THIS
        // process, so the common case (the operator just launched the harness run and the
        // game has focus) is already-foreground and needs no call at all. For the case that
        // matters - another window took focus mid-run - the least invasive documented
        // workaround is AttachThreadInput: attach this thread's input queue to the
        // foreground window's thread, call SetForegroundWindow, detach. It is preferred over
        // SwitchToThisWindow (undocumented, and it restores / animates the window) and over
        // AllowSetForegroundWindow (which must be called by the CURRENT foreground process,
        // not by us).

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd,
                                                            IntPtr processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
                                                     bool fAttach);

        // ----- nudge= -----
        //
        // SendInput with MOUSEEVENTF_MOVE and no ABSOLUTE flag is a RELATIVE move, which is
        // what makes it an EVENT rather than a warp: the OS synthesises the same input a
        // mouse would, so the window's message queue receives a real WM_MOUSEMOVE.
        // SetCursorPos does not - it writes the cursor position and Unity's per-frame sample
        // follows it, while the event stream (which is what an IMGUI hover is computed
        // during) sees nothing. The (+1,0) / (-1,0) pair cancels out, so the cursor ends
        // where the move put it and the read-back still compares against the commanded
        // point.

        private const uint InputMouse = 0;
        private const uint MouseEventMove = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32MouseInput
        {
            internal int Dx;
            internal int Dy;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Input
        {
            internal uint Type;
            internal Win32MouseInput Mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, Win32Input[] inputs,
                                             int sizeOfInputStructure);

        /// <summary>The stock game window title, used as the second lookup. Not a guess:
        /// KSP 1.12.5 titles its player window with the product name, and this literal is
        /// the fallback for a host whose <c>Application.productName</c> has been changed by
        /// a launcher.</summary>
        private const string KspWindowTitle = "Kerbal Space Program";

        // ----- execute -----

        private void UiActionPointerOp(ParsedCommand cmd)
        {
            if (!TestCommandUiPointer.TryParseRequest(
                    ArgOrNull(cmd, TestCommandUiPointer.XArg),
                    ArgOrNull(cmd, TestCommandUiPointer.YArg),
                    ArgOrNull(cmd, TestCommandUiPointer.ParkArg),
                    ArgOrNull(cmd, TestCommandUiPointer.FocusArg),
                    ArgOrNull(cmd, TestCommandUiPointer.NudgeArg),
                    out UiPointerRequest request, out string reject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} "
                    + $"x={ArgOrNull(cmd, TestCommandUiPointer.XArg) ?? string.Empty} "
                    + $"y={ArgOrNull(cmd, TestCommandUiPointer.YArg) ?? string.Empty} "
                    + $"park={ArgOrNull(cmd, TestCommandUiPointer.ParkArg) ?? string.Empty} "
                    + $"focus={ArgOrNull(cmd, TestCommandUiPointer.FocusArg) ?? string.Empty} "
                    + $"nudge={ArgOrNull(cmd, TestCommandUiPointer.NudgeArg) ?? string.Empty}");
                SetExecResult("REJECTED", null, reject);
                return;
            }

            // PRE-CALL platform gate, named rather than left to a DllNotFoundException: a
            // cloud / Linux host reading `pointer-unsupported-platform` knows this is the
            // seam declining, not a Parsek defect.
            if (!IsWindowsRuntime())
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.UnsupportedPlatformReason
                    + $" platform={Application.platform}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiPointer.UnsupportedPlatformReason} "
                    + $"platform={Application.platform}");
                return;
            }

            int screenW = Screen.width;
            int screenH = Screen.height;
            if (!TestCommandUiPointer.IsInsideClient(request.X, request.Y, screenW, screenH))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiPointer.OffScreenReason
                    + $" x={Fmt(request.X)} y={Fmt(request.Y)} "
                    + $"client={Int(screenW)}x{Int(screenH)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiPointer.OffScreenReason} x={Fmt(request.X)} "
                    + $"y={Fmt(request.Y)} client={Int(screenW)}x{Int(screenH)}");
                return;
            }

            int screenX, screenY;
            string via;
            IntPtr gameWindow;
            if (!TryResolveDesktopPoint((int)request.X, (int)request.Y,
                                        out screenX, out screenY, out via,
                                        out gameWindow))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.WindowUnresolvedReason
                    + $" x={Fmt(request.X)} y={Fmt(request.Y)}");
                SetExecResult("ERROR", null, TestCommandUiPointer.WindowUnresolvedReason);
                return;
            }

            // THE OPERATOR LINE. A harness run happens on the operator's own desktop and
            // this yanks his cursor; there is no way to hover without doing it, because a
            // cursor is a single machine-wide resource. Logged EVERY time (not rate-limited,
            // not verbose-gated) so a run whose captures look wrong can be read back against
            // "someone was using the mouse", and so the count of moves in a lane is
            // recoverable from KSP.log alone.
            ParsekLog.Info(Tag, $"uiaction pointer moving the OS cursor to client "
                + $"{Fmt(request.X)},{Fmt(request.Y)} (desktop {Int(screenX)},{Int(screenY)}"
                + $" via={via}); park={Bool(request.Park)} focus={Bool(request.Focus)} "
                + $"nudge={Bool(request.Nudge)}. This is machine-wide: an "
                + "operator using the mouse during the run will see it jump, and his own "
                + "next move invalidates any hover this step set up"
                + (request.Focus
                    ? "; focus=true additionally STEALS the foreground from whatever he is "
                      + "doing"
                    : string.Empty)
                + (request.Nudge
                    ? "; nudge=true synthesises a mouse move at the system level"
                    : string.Empty));

            // BEFORE the move, by contract: the point of focus=true is that the window owns
            // the input queue WHEN the move lands, so a foreground taken afterwards would
            // prove nothing about the event that carried the cursor.
            UiPointerFocusOutcome focusOutcome =
                request.Focus ? TryTakeForeground(gameWindow) : UiPointerFocusOutcome.NotRequested;

            bool issued;
            try
            {
                issued = SetCursorPos(screenX, screenY);
            }
            catch (Exception ex)
            {
                // A bind failure here means the platform gate above was wrong about the
                // host, which is worth naming rather than reporting as a generic throw.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.UnsupportedPlatformReason
                    + $" SetCursorPos threw {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null,
                    TestCommandUiPointer.UnsupportedPlatformReason);
                return;
            }
            if (!issued)
            {
                // SetCursorPos returns false when the calling process is not allowed to
                // move the cursor (a UIPI / secure-desktop condition). The move did not
                // happen, so the hover cannot have.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.NotAppliedReason
                    + " SetCursorPos returned false (the OS refused the move)");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiPointer.NotAppliedReason} os-refused");
                return;
            }

            // AFTER the move, and only then: a relative nudge is measured from wherever the
            // cursor now is, so sending it first would land it one pixel off the control.
            bool nudgeApplied = request.Nudge && TrySendRelativeNudge();

            // The probe fires on the next strip Repaint, which is the pass a hover would
            // have painted in. Armed for EVERY move (not only under the flags) because the
            // measurement it takes - what Event.current.mousePosition reads inside a Parsek
            // OnGUI beside Input.mousePosition - is the discriminator for
            // GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT, and it is Verbose.
            TooltipEchoStripLatch.ArmMousePositionProbe();

            bool foregroundIsGame = gameWindow != IntPtr.Zero
                                    && ReadForegroundWindowSafely() == gameWindow;

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Pointer,
                Window = null,
                StartFrame = Time.frameCount,
                PointerX = request.X,
                PointerY = request.Y,
                PointerPark = request.Park,
                PointerScreenX = screenX,
                PointerScreenY = screenY,
                PointerVia = via,
                PointerFocus = request.Focus,
                PointerNudge = nudgeApplied,
                PointerFocusOutcome = focusOutcome,
                PointerForegroundIsGame = foregroundIsGame,
            };
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- settle -----

        /// <summary>
        /// The pointer op's OWN settle poll, routed from <c>TryCompleteUiAction</c>'s head
        /// beside <c>find</c>'s rather than reached through the shared preamble.
        ///
        /// <para>It re-reads <c>Input.mousePosition</c> on EVERY poll until it agrees with
        /// the commanded point or the verb's budget runs out. The single read it replaced
        /// made a one-frame input lag - the OS cursor moved, Unity had not yet sampled it -
        /// indistinguishable from an unfocused game, and answered the hard
        /// <c>pointer-not-applied</c> ERROR over a move that was about to be correct. The
        /// frame count is now a FLOOR (a cursor that arrived before an IMGUI pass has
        /// hovered nothing yet), not the signal. Decision:
        /// <c>TestCommandUiPointer.DecidePoll</c>.</para>
        /// </summary>
        private void TryCompleteUiActionPointer(double now)
        {
            int screenH = Screen.height;
            Vector3 mouse = Input.mousePosition;
            float observedGuiY = TestCommandUiPointer.GuiYFromUnityMouseY(screenH, mouse.y);
            bool landed = TestCommandUiPointer.LandedWithinTolerance(
                uiActionPending.PointerX, uiActionPending.PointerY, mouse.x, observedGuiY);
            int framesElapsed = Time.frameCount - uiActionPending.StartFrame;
            double budget = DeferralBudget.BudgetSeconds("UiAction");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);

            UiActionSettleOutcome outcome = TestCommandUiPointer.DecidePoll(
                landed, framesElapsed, TestCommandUiAction.SettleFrames, expired);
            if (outcome == UiActionSettleOutcome.NotYet)
                return;

            string id = completionId;
            long seq = completionSeq;
            string verb = completionVerb;
            UiActionPending pending = uiActionPending;
            ClearTwoPhase();

            if (outcome != UiActionSettleOutcome.Settled)
            {
                // The move was issued, the budget is gone, and the engine still does not
                // agree about where the cursor is. The two live causes are a game window
                // that is not receiving input and a display-scaling mismatch between
                // desktop and client pixels; both mean the hover this step exists to
                // produce did not happen. `frames=` is now the POLL count, which is what
                // separates "never sampled" from "sampled and wrong".
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.NotAppliedReason
                    + $" want={Fmt(pending.PointerX)},{Fmt(pending.PointerY)} "
                    + $"after={Fmt(mouse.x)},{Fmt(observedGuiY)} "
                    + $"desktop={Int(pending.PointerScreenX)},{Int(pending.PointerScreenY)} "
                    + $"via={pending.PointerVia} client={Int(Screen.width)}x{Int(screenH)} "
                    + $"focused={Bool(Application.isFocused)} frames={Int(framesElapsed)}; "
                    + "an unfocused game does not update Input.mousePosition, and the same "
                    + "condition means no hover would have painted");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiPointer.NotAppliedReason} "
                    + $"want={Fmt(pending.PointerX)},{Fmt(pending.PointerY)} "
                    + $"after={Fmt(mouse.x)},{Fmt(observedGuiY)}",
                    dequeueHead: true);
                return;
            }

            // KEY ORDER IS LOAD-BEARING up to `via=`: the wave-2 census lanes' log contracts
            // pin `uiaction pointer at=<x>,<y> park=<b> via=` and stop there, so the five
            // flag keys are APPENDED after it and every one of those regexes still matches.
            // `tooltip=` is the reading the whole flag pair exists to produce: the hover-echo
            // strip's text as of the settled frame, `-` when the strip is empty - which is
            // what four census captures photographed with no way to say so in the log.
            // RE-ARM, after the read-back agreed. The arm at execute time fires on the
            // first strip Repaint after the move was ISSUED, which run 2026-09-15_1520
            // measured landing one frame early: its `input=` read the PREVIOUS position
            // while the cursor was still in flight. A second probe, armed once the cursor
            // is confirmed on the control, is the apples-to-apples reading - and the arm is
            // idempotent, so the pair costs one extra Verbose line.
            TooltipEchoStripLatch.ArmMousePositionProbe();

            string tooltip = TooltipEchoStripLatch.LastText;
            ParsekLog.Info(Tag, $"uiaction pointer at={Fmt(mouse.x)},{Fmt(observedGuiY)} "
                + $"park={Bool(pending.PointerPark)} via={pending.PointerVia} "
                + $"frames={Int(framesElapsed)} focus={Bool(pending.PointerFocus)} "
                + $"nudge={Bool(pending.PointerNudge)} "
                + "fgOutcome="
                + TestCommandUiPointer.FocusOutcomeToken(pending.PointerFocusOutcome)
                + $" fg={Bool(pending.PointerForegroundIsGame)} "
                + $"tooltip={TooltipEchoStripLatch.FormatForLog(tooltip)} "
                + $"tooltipFrame={Int(TooltipEchoStripLatch.LastFrame)}");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiPointer.BuildPayload(
                    mouse.x, observedGuiY, pending.PointerPark,
                    pending.PointerScreenX, pending.PointerScreenY, pending.PointerVia,
                    pending.PointerFocus, pending.PointerNudge,
                    pending.PointerFocusOutcome, pending.PointerForegroundIsGame,
                    tooltip, TooltipEchoStripLatch.LastFrame),
                null, dequeueHead: true);
        }

        // ----- plumbing -----

        private static bool IsWindowsRuntime()
            => Application.platform == RuntimePlatform.WindowsPlayer
               || Application.platform == RuntimePlatform.WindowsEditor;

        /// <summary>
        /// Converts a client point to a desktop point, trying the window-handle chain in
        /// order and reporting which rung answered. See the class header for why
        /// <c>GetForegroundWindow</c> is not in the chain and why the assumed-origin
        /// fallback is safe.
        /// </summary>
        private static bool TryResolveDesktopPoint(int clientX, int clientY,
                                                   out int screenX, out int screenY,
                                                   out string via, out IntPtr gameWindow)
        {
            screenX = clientX;
            screenY = clientY;
            via = "assumed-origin";
            gameWindow = IntPtr.Zero;

            IntPtr hwnd = IntPtr.Zero;
            string source = null;
            try
            {
                hwnd = GetActiveWindow();
                if (hwnd != IntPtr.Zero) source = "active";
                if (hwnd == IntPtr.Zero)
                {
                    string product = Application.productName;
                    if (!string.IsNullOrEmpty(product))
                    {
                        hwnd = FindWindow(null, product);
                        if (hwnd != IntPtr.Zero) source = "find-product";
                    }
                }
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = FindWindow(null, KspWindowTitle);
                    if (hwnd != IntPtr.Zero) source = "find-title";
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"uiaction pointer window lookup threw "
                    + $"{ex.GetType().Name}: {ex.Message}; falling back to an assumed "
                    + "client origin of 0,0 (correct for exclusive fullscreen; the "
                    + "read-back catches it when it is not)");
                return true;
            }

            if (hwnd == IntPtr.Zero)
            {
                ParsekLog.Warn(Tag, "uiaction pointer could not find the game window "
                    + "(GetActiveWindow returned 0 - the game does not have focus - and "
                    + "neither title matched); falling back to an assumed client origin of "
                    + "0,0");
                return true;
            }

            var point = new Win32Point { X = clientX, Y = clientY };
            bool ok;
            try
            {
                ok = ClientToScreen(hwnd, ref point);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"uiaction pointer ClientToScreen threw "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
            if (!ok)
            {
                ParsekLog.Error(Tag, "uiaction pointer ClientToScreen returned false for "
                    + $"hwnd={hwnd.ToInt64()} via={source}");
                return false;
            }
            screenX = point.X;
            screenY = point.Y;
            via = source;
            gameWindow = hwnd;
            return true;
        }

        /// <summary>
        /// <c>GetForegroundWindow</c> with the bind / UIPI failure contained. Zero when it
        /// cannot be asked, which the callers treat as "not the game" - the conservative
        /// direction: an unknown foreground must never be reported as a confirmed one.
        /// </summary>
        private static IntPtr ReadForegroundWindowSafely()
        {
            try
            {
                return GetForegroundWindow();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"uiaction pointer GetForegroundWindow threw "
                    + $"{ex.GetType().Name}: {ex.Message}; reporting fg=false");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Brings the resolved game window to the foreground, reporting WHICH rung answered
        /// (<see cref="UiPointerFocusOutcome"/>). Never an ERROR: a refused foreground is a
        /// measurement, and the move plus the read-back remain the op's verdict.
        ///
        /// <para>Rung 0 is "already foreground", which costs no call and is the expected
        /// reading on an operator desktop that just launched the game - and it is the answer
        /// that makes a still-unpainted hover NOT a foreground problem. Rung 1 is a plain
        /// <c>SetForegroundWindow</c>. Rung 2 is the documented <c>AttachThreadInput</c>
        /// workaround, used only when rung 1 was refused: Windows permits
        /// <c>SetForegroundWindow</c> from a process that does not own the foreground only
        /// under conditions we may not meet mid-run, and attaching this thread's input queue
        /// to the current foreground thread supplies one of them. The attach is detached in
        /// a <c>finally</c>, because leaving two input queues attached would couple the
        /// game's input to another process's for the rest of the session.</para>
        /// </summary>
        private static UiPointerFocusOutcome TryTakeForeground(IntPtr gameWindow)
        {
            if (gameWindow == IntPtr.Zero)
            {
                ParsekLog.Warn(Tag, "uiaction pointer focus=true but no game window handle "
                    + "was resolved (the assumed-origin fallback rung), so the foreground "
                    + "could not be asked for; the move still goes ahead");
                return TestCommandUiPointer.ClassifyFocus(true, false, false, false, false);
            }

            try
            {
                IntPtr before = ReadForegroundWindowSafely();
                if (before == gameWindow)
                {
                    ParsekLog.Info(Tag, "uiaction pointer focus=true was a no-op: the game "
                        + "window is ALREADY the foreground window, so an unpainted hover "
                        + "after this move is not a foreground problem");
                    return TestCommandUiPointer.ClassifyFocus(true, true, true, false, false);
                }

                SetForegroundWindow(gameWindow);
                bool directTookIt = ReadForegroundWindowSafely() == gameWindow;
                if (directTookIt)
                {
                    ParsekLog.Info(Tag, "uiaction pointer focus=true took the foreground "
                        + "with a plain SetForegroundWindow");
                    return TestCommandUiPointer.ClassifyFocus(true, true, false, true, false);
                }

                // Rung 2: attach this thread's input queue to the foreground window's
                // thread, which supplies one of the documented conditions under which
                // SetForegroundWindow is permitted, then detach unconditionally.
                bool attachTookIt = false;
                uint ourThread = GetCurrentThreadId();
                uint foregroundThread =
                    GetWindowThreadProcessId(before, IntPtr.Zero);
                bool attached = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != ourThread)
                        attached = AttachThreadInput(ourThread, foregroundThread, true);
                    SetForegroundWindow(gameWindow);
                    attachTookIt = ReadForegroundWindowSafely() == gameWindow;
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(ourThread, foregroundThread, false);
                }

                if (attachTookIt)
                {
                    ParsekLog.Info(Tag, "uiaction pointer focus=true took the foreground "
                        + "through the AttachThreadInput path (a plain SetForegroundWindow "
                        + "was refused)");
                    return TestCommandUiPointer.ClassifyFocus(true, true, false, false, true);
                }

                ParsekLog.Warn(Tag, "uiaction pointer focus=true did NOT take the "
                    + "foreground: both SetForegroundWindow and the AttachThreadInput "
                    + "retry were refused, so the window still does not own the input "
                    + "queue. The move goes ahead and the read-back is still the verdict");
                return TestCommandUiPointer.ClassifyFocus(true, true, false, false, false);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"uiaction pointer focus=true threw "
                    + $"{ex.GetType().Name}: {ex.Message}; reporting a refused foreground "
                    + "and continuing with the move");
                return TestCommandUiPointer.ClassifyFocus(true, true, false, false, false);
            }
        }

        /// <summary>
        /// Sends ONE relative <c>(+1,0)</c> then <c>(-1,0)</c> mouse move through
        /// <c>SendInput</c>, so the window receives a real <c>WM_MOUSEMOVE</c> pair rather
        /// than only a new cursor position. Returns whether the OS accepted both.
        ///
        /// <para>The pair cancels, so the cursor ends where the preceding
        /// <c>SetCursorPos</c> put it. Both events go in ONE <c>SendInput</c> call, which is
        /// the documented way to keep another process's input from being interleaved
        /// between them - an interleaved move would leave the cursor one pixel off the
        /// control the step resolved.</para>
        /// </summary>
        private static bool TrySendRelativeNudge()
        {
            try
            {
                var inputs = new[]
                {
                    NewRelativeMove(1, 0),
                    NewRelativeMove(-1, 0),
                };
                uint sent = SendInput((uint)inputs.Length, inputs,
                                      Marshal.SizeOf(typeof(Win32Input)));
                if (sent == inputs.Length)
                {
                    ParsekLog.Info(Tag, "uiaction pointer nudge=true sent a relative "
                        + "(+1,0)/(-1,0) SendInput pair, so the window received a real "
                        + "WM_MOUSEMOVE at the landed position");
                    return true;
                }
                int lastError = Marshal.GetLastWin32Error();
                ParsekLog.Warn(Tag, $"uiaction pointer nudge=true: SendInput accepted "
                    + $"{sent} of {inputs.Length} events, lastError={Int(lastError)}, so no "
                    + "mouse event reached the window; reporting nudge=false. The error code "
                    + "is reported rather than guessed at: this file exists to measure, and "
                    + "a UIPI block, a foreground input block and an unsupported call are "
                    + "different findings");
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"uiaction pointer nudge=true: SendInput threw "
                    + $"{ex.GetType().Name}: {ex.Message}; reporting nudge=false");
                return false;
            }
        }

        private static Win32Input NewRelativeMove(int dx, int dy)
            => new Win32Input
            {
                Type = InputMouse,
                Mouse = new Win32MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = 0,
                    // No MOUSEEVENTF_ABSOLUTE: without it the OS treats dx/dy as a RELATIVE
                    // move, which is the whole point - an absolute SendInput would be
                    // another warp, in normalised 0..65535 coordinates, and would produce
                    // the same non-event SetCursorPos already produces.
                    Flags = MouseEventMove,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };

        private static string Fmt(float v)
            => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
