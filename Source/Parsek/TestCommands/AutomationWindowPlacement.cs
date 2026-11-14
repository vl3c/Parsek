using System;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>What an automation env value said: absent, usable, or present but unusable.</summary>
    internal enum PlacementEnvState
    {
        Unset,
        Valid,
        Invalid,
    }

    /// <summary>
    /// The window placement the harness asks for at boot, read once from the env when the
    /// command seam is armed.
    /// </summary>
    internal struct WindowPlacementConfig
    {
        internal PlacementEnvState DesktopState;
        internal Guid DesktopId;
        internal PlacementEnvState ReturnState;
        internal long ReturnHwnd;

        internal bool WantsMove => DesktopState == PlacementEnvState.Valid;
        internal bool WantsReturn => ReturnState == PlacementEnvState.Valid;
    }

    /// <summary>
    /// The OS calls the placement run needs. The live implementation is
    /// <see cref="AutomationWindowPlacementNative"/>; tests pass a fake. Every member must
    /// answer without throwing (a failed call reports through its return value).
    /// </summary>
    internal interface IWindowPlacementPlatform
    {
        bool IsWindows { get; }
        IntPtr FindOwnWindow(out bool visible);
        int MoveWindowToDesktop(IntPtr hwnd, Guid desktopId, out string error);
        IntPtr GetForegroundWindow();
        bool IsWindow(IntPtr hwnd);
        bool SetForegroundWindow(IntPtr hwnd);
    }

    /// <summary>
    /// Automation only (armed by <c>PARSEK_TEST_COMMANDS=1</c>): moves this KSP's own window
    /// to the virtual desktop the harness names and hands the foreground back to the window
    /// that had it at launch, so parallel harness copies stay off the operator's desktop.
    ///
    /// <para>Env, read once at seam Awake: <c>PARSEK_AUTOMATION_DESKTOP_ID</c> (a virtual
    /// desktop GUID) and <c>PARSEK_AUTOMATION_RETURN_FOREGROUND</c> (a decimal HWND). The
    /// move is the documented <c>IVirtualDesktopManager.MoveWindowToDesktop</c>, which only
    /// works on the caller's own windows, which is why it runs in the game process. A
    /// foreground process may hand the foreground away, so the return is a plain
    /// <c>SetForegroundWindow</c> made only while this window is foreground.</para>
    ///
    /// <para>The run is driven one frame at a time by the seam addon (<see cref="Step"/>)
    /// and ends with exactly one Info line, <see cref="FormatLine"/>:
    /// <c>window placement desktop=&lt;moved|failed hr=0x........|skipped|no-window&gt;
    /// foreground=&lt;returned|not-foreground|failed|skipped&gt; ...</c>. Report-only: the
    /// harness minimises the window itself whenever the line does not say
    /// <c>desktop=moved</c>, and nothing fails a lane from here.</para>
    /// </summary>
    internal sealed class WindowPlacementRun
    {
        internal const string DesktopIdEnvVar = "PARSEK_AUTOMATION_DESKTOP_ID";
        internal const string ReturnForegroundEnvVar = "PARSEK_AUTOMATION_RETURN_FOREGROUND";
        internal const string LinePrefix = "window placement ";

        /// <summary>How long the run waits for a visible window and retries the move.</summary>
        internal const double WindowWaitSeconds = 10.0;

        /// <summary>Spacing between two move attempts (the desktop manager can refuse a
        /// window it has not registered yet, right after the window is shown).</summary>
        internal const double MoveRetrySeconds = 0.25;

        /// <summary>How long after the move phase the run waits for this window to be
        /// foreground before it reports not-foreground (the launch focus grab lands a
        /// moment after the window shows).</summary>
        internal const double ForegroundGraceSeconds = 2.0;

        private readonly WindowPlacementConfig config;
        private readonly double startSeconds;

        private bool done;
        private IntPtr window;
        private bool moveSettled;
        private bool moved;
        private int moveAttempts;
        private int lastHr;
        private string lastError;
        private double nextMoveAt;
        private double foregroundDeadline = -1.0;
        private string desktopToken;
        private string foregroundToken;
        private string foregroundReason;
        private double finishedElapsed;

        internal WindowPlacementRun(WindowPlacementConfig config, double startSeconds)
        {
            this.config = config;
            this.startSeconds = startSeconds;
        }

        internal bool Done => done;

        /// <summary>The one log line; null until <see cref="Done"/>.</summary>
        internal string Line { get; private set; }

        // ----- pure config -----

        /// <summary>Pure: a desktop GUID env value. Empty or whitespace is Unset; an
        /// unparsable value or the empty GUID is Invalid.</summary>
        internal static PlacementEnvState ParseDesktopId(string raw, out Guid id)
        {
            id = Guid.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return PlacementEnvState.Unset;
            Guid parsed;
            if (!TryParseGuid(raw.Trim(), out parsed) || parsed == Guid.Empty)
                return PlacementEnvState.Invalid;
            id = parsed;
            return PlacementEnvState.Valid;
        }

        private static bool TryParseGuid(string text, out Guid value)
        {
            try
            {
                value = new Guid(text);
                return true;
            }
            catch (FormatException)
            {
                value = Guid.Empty;
                return false;
            }
            catch (OverflowException)
            {
                value = Guid.Empty;
                return false;
            }
        }

        /// <summary>Pure: a decimal HWND env value. Empty is Unset; anything that is not a
        /// positive decimal integer is Invalid.</summary>
        internal static PlacementEnvState ParseReturnHwnd(string raw, out long hwnd)
        {
            hwnd = 0;
            if (string.IsNullOrWhiteSpace(raw)) return PlacementEnvState.Unset;
            long parsed;
            if (!long.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                || parsed <= 0)
                return PlacementEnvState.Invalid;
            hwnd = parsed;
            return PlacementEnvState.Valid;
        }

        /// <summary>Pure: the config from the two raw env values.</summary>
        internal static WindowPlacementConfig ParseConfig(string desktopRaw, string returnRaw)
        {
            var config = new WindowPlacementConfig();
            config.DesktopState = ParseDesktopId(desktopRaw, out config.DesktopId);
            config.ReturnState = ParseReturnHwnd(returnRaw, out config.ReturnHwnd);
            return config;
        }

        /// <summary>Pure: an HRESULT as the line spells it, <c>0x</c> + eight upper-case hex digits.</summary>
        internal static string FormatHr(int hr)
            => "0x" + unchecked((uint)hr).ToString("X8", CultureInfo.InvariantCulture);

        /// <summary>Pure: the one log line.</summary>
        internal static string FormatLine(string desktopToken, int hr, string foregroundToken,
            long hwnd, int moveAttempts, double elapsedSeconds, WindowPlacementConfig config,
            string error, string foregroundReason)
        {
            var sb = new StringBuilder(LinePrefix);
            sb.Append("desktop=").Append(desktopToken ?? "skipped");
            if (desktopToken == "failed")
                sb.Append(" hr=").Append(FormatHr(hr));
            sb.Append(" foreground=").Append(foregroundToken ?? "skipped");
            sb.Append(" hwnd=0x").Append(hwnd.ToString("X", CultureInfo.InvariantCulture));
            sb.Append(" moveAttempts=").Append(moveAttempts.ToString(CultureInfo.InvariantCulture));
            sb.Append(" elapsedSec=").Append(elapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture));
            if (config.DesktopState == PlacementEnvState.Invalid)
                sb.Append(" desktopEnv=invalid");
            if (config.ReturnState == PlacementEnvState.Invalid)
                sb.Append(" returnEnv=invalid");
            if (!string.IsNullOrEmpty(foregroundReason))
                sb.Append(" fgReason=").Append(foregroundReason);
            if (!string.IsNullOrEmpty(error))
                sb.Append(" error=").Append(SanitizeToken(error));
            return sb.ToString();
        }

        private static string SanitizeToken(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                sb.Append(char.IsWhiteSpace(c) ? '_' : c);
            return sb.ToString();
        }

        // ----- the per-frame run -----

        /// <summary>
        /// Advances the run by one frame at <paramref name="nowSeconds"/> (any monotonic
        /// clock, the same one <c>startSeconds</c> was read from). Returns true once the run
        /// has finished and <see cref="Line"/> is set.
        /// </summary>
        internal bool Step(double nowSeconds, IWindowPlacementPlatform platform)
        {
            if (done) return true;
            double elapsed = nowSeconds - startSeconds;

            if (!config.WantsMove && !config.WantsReturn)
                return Finish(elapsed, "skipped", "skipped", null);
            if (platform == null || !platform.IsWindows)
                return Finish(elapsed, "skipped", "skipped", "not-windows");

            if (window == IntPtr.Zero || !moveSettled)
            {
                bool visible;
                IntPtr found = platform.FindOwnWindow(out visible);
                if (found == IntPtr.Zero)
                {
                    if (elapsed >= WindowWaitSeconds)
                        return Finish(elapsed, config.WantsMove ? "no-window" : "skipped", "skipped", null);
                    return false;
                }
                if (!visible && elapsed < WindowWaitSeconds)
                    return false;
                window = found;
            }

            if (!moveSettled)
            {
                if (!config.WantsMove)
                {
                    moveSettled = true;
                }
                else
                {
                    if (nowSeconds >= nextMoveAt)
                    {
                        moveAttempts++;
                        string error;
                        int hr = platform.MoveWindowToDesktop(window, config.DesktopId, out error);
                        if (hr == 0)
                        {
                            moved = true;
                            moveSettled = true;
                            lastError = null;
                        }
                        else
                        {
                            lastHr = hr;
                            lastError = error;
                            nextMoveAt = nowSeconds + MoveRetrySeconds;
                        }
                    }
                    if (!moveSettled)
                    {
                        if (elapsed < WindowWaitSeconds) return false;
                        moveSettled = true;
                    }
                }
            }

            desktopToken = !config.WantsMove ? "skipped" : (moved ? "moved" : "failed");

            if (!config.WantsReturn)
                return Finish(elapsed, desktopToken, "skipped", null);

            var returnHwnd = new IntPtr(config.ReturnHwnd);
            if (foregroundDeadline < 0)
            {
                foregroundDeadline = nowSeconds + ForegroundGraceSeconds;
                if (!platform.IsWindow(returnHwnd))
                    return Finish(elapsed, desktopToken, "failed", "return-window-gone");
            }

            if (platform.GetForegroundWindow() == window)
            {
                bool ok = platform.SetForegroundWindow(returnHwnd);
                return Finish(elapsed, desktopToken, ok ? "returned" : "failed",
                    ok ? null : "set-foreground-refused");
            }
            if (nowSeconds >= foregroundDeadline)
                return Finish(elapsed, desktopToken, "not-foreground", null);
            return false;
        }

        /// <summary>Ends the run after an unexpected exception in the frame driver.</summary>
        internal void FinishOnException(double nowSeconds, Exception ex)
        {
            if (done) return;
            if (lastError == null && ex != null) lastError = ex.GetType().Name;
            if (lastHr == 0) lastHr = unchecked((int)0x80004005);
            string desk = !config.WantsMove ? "skipped" : (moved ? "moved" : "failed");
            Finish(nowSeconds - startSeconds, desk, foregroundToken ?? "skipped", "exception");
        }

        private bool Finish(double elapsed, string desk, string fg, string fgReason)
        {
            done = true;
            desktopToken = desk;
            foregroundToken = fg;
            foregroundReason = fgReason;
            finishedElapsed = elapsed < 0 ? 0 : elapsed;
            Line = FormatLine(desktopToken, lastHr, foregroundToken, window.ToInt64(),
                moveAttempts, finishedElapsed, config, desk == "failed" ? lastError : null,
                foregroundReason);
            return true;
        }
    }
}
