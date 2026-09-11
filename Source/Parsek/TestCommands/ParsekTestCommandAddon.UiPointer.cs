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
                    out UiPointerRequest request, out string reject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} "
                    + $"x={ArgOrNull(cmd, TestCommandUiPointer.XArg) ?? string.Empty} "
                    + $"y={ArgOrNull(cmd, TestCommandUiPointer.YArg) ?? string.Empty} "
                    + $"park={ArgOrNull(cmd, TestCommandUiPointer.ParkArg) ?? string.Empty}");
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
            if (!TryResolveDesktopPoint((int)request.X, (int)request.Y,
                                        out screenX, out screenY, out via))
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
                + $" via={via}); park={Bool(request.Park)}. This is machine-wide: an "
                + "operator using the mouse during the run will see it jump, and his own "
                + "next move invalidates any hover this step set up");

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
            };
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- settle -----

        private void CompleteUiActionPointer(UiActionSettleContext ctx,
                                             UiActionPending pending)
        {
            int screenH = Screen.height;
            Vector3 mouse = Input.mousePosition;
            float observedGuiY = TestCommandUiPointer.GuiYFromUnityMouseY(screenH, mouse.y);
            if (!TestCommandUiPointer.LandedWithinTolerance(
                    pending.PointerX, pending.PointerY, mouse.x, observedGuiY))
            {
                // The move was issued and the engine does not agree about where the cursor
                // is. The two live causes are a game window that is not receiving input and
                // a display-scaling mismatch between desktop and client pixels; both mean
                // the hover this step exists to produce did not happen.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiPointer.NotAppliedReason
                    + $" want={Fmt(pending.PointerX)},{Fmt(pending.PointerY)} "
                    + $"after={Fmt(mouse.x)},{Fmt(observedGuiY)} "
                    + $"desktop={Int(pending.PointerScreenX)},{Int(pending.PointerScreenY)} "
                    + $"via={pending.PointerVia} client={Int(Screen.width)}x{Int(screenH)} "
                    + $"focused={Bool(Application.isFocused)} frames={Int(ctx.Frames)}; "
                    + "an unfocused game does not update Input.mousePosition, and the same "
                    + "condition means no hover would have painted");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiPointer.NotAppliedReason} "
                    + $"want={Fmt(pending.PointerX)},{Fmt(pending.PointerY)} "
                    + $"after={Fmt(mouse.x)},{Fmt(observedGuiY)}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction pointer at={Fmt(mouse.x)},{Fmt(observedGuiY)} "
                + $"park={Bool(pending.PointerPark)} via={pending.PointerVia} "
                + $"frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiPointer.BuildPayload(
                    mouse.x, observedGuiY, pending.PointerPark,
                    pending.PointerScreenX, pending.PointerScreenY, pending.PointerVia),
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
                                                   out string via)
        {
            screenX = clientX;
            screenY = clientY;
            via = "assumed-origin";

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
            return true;
        }

        private static string Fmt(float v)
            => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
