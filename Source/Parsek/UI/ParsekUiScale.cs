using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Makes every Parsek IMGUI window, popup and tooltip follow KSP's UI Scale slider
    /// (<c>GameSettings.UI_SCALE</c>), with no Parsek setting of its own (owner ruling
    /// 2026-11-04, todo S10).
    ///
    /// <para><b>How.</b> A draw host (the Flight and Space Center controllers, the
    /// global test runner, the watch-mode readout, the currency hover) opens a
    /// <see cref="BeginHost"/> scope around its window draws. The scope sets
    /// <c>GUI.matrix</c> to a uniform scale, so every rect, font and texture drawn inside
    /// it grows by the same factor and Unity maps the IMGUI mouse back through the
    /// matrix. Rects inside a scope therefore live in SCALED-ROOT units: the screen is
    /// <c>Screen.width / scale</c> wide (<see cref="ScaledScreenWidth"/>). At scale 1 the
    /// scope leaves <c>GUI.matrix</c> untouched, so 100% draws exactly as before.</para>
    ///
    /// <para><b>The three reads a matrix does not fix.</b> (1) <c>Screen.width</c> /
    /// <c>Screen.height</c> are physical pixels; a clamp inside a scope reads
    /// <see cref="ScaledScreenWidth"/> / <see cref="ScaledScreenHeight"/>. (2)
    /// <c>GUIUtility.GUIToScreenPoint</c> applies the matrix and returns physical pixels;
    /// a popup opened at that point and drawn back inside the scope must use
    /// <see cref="GuiPointToScaledRoot"/>. (3) ClickThroughBlocker's hit test compares
    /// the raw <c>Input.mousePosition</c> with the rect the window call returns, which is
    /// in scaled-root units, so under a scale it would block clicks over the wrong part
    /// of the screen. <see cref="Window"/> therefore keeps ClickThroughBlocker at scale 1
    /// and, under any other scale, draws through <c>GUILayout.Window</c> and holds the
    /// same <c>ALLBUTCAMERAS</c> lock itself, hit-tested in scaled-root units.</para>
    ///
    /// <para>Not scaled, by the ruling: map markers and ghost name labels, which are
    /// projected from world positions in physical pixels.</para>
    /// </summary>
    internal static class ParsekUiScale
    {
        private const string Tag = "UIScale";

        /// <summary>Floor and ceiling for the applied scale. The stock slider spans a
        /// narrower range; these only stop a hand-edited settings.cfg value from drawing
        /// windows too small to read or larger than the screen.</summary>
        internal const float MinScale = 0.5f;
        internal const float MaxScale = 3f;

        /// <summary>A setting within this of 1 counts as 1, so float noise from
        /// settings.cfg never turns the matrix on.</summary>
        internal const float UnitySnap = 0.001f;

        internal const string ScaledLockPrefix = "ParsekScaledWindow_";

        /// <summary>Automation and in-game test override; null in every player build.</summary>
        internal static float? OverrideForTesting;

        /// <summary>The scale of the innermost open host scope; 1 outside any scope.</summary>
        internal static float ActiveScale { get; private set; } = 1f;

        // Per host, so two hosts drawing at different scales (an in-game probe at a
        // fixed scale beside the live windows) never flap one shared line every frame.
        private static readonly Dictionary<string, float> lastLoggedScaleByHost =
            new Dictionary<string, float>();

        private sealed class ScaledLockState
        {
            internal bool Held;
            internal int LastDrawFrame;
            internal string Title;
        }

        private static readonly Dictionary<int, ScaledLockState> scaledLocks =
            new Dictionary<int, ScaledLockState>();

        private static readonly Dictionary<int, float> lastDrawScaleByWindow =
            new Dictionary<int, float>();

        // ----- pure -----

        /// <summary>The scale to apply for a raw <c>UI_SCALE</c> value: 1 for a missing,
        /// non-finite or non-positive value, otherwise clamped to
        /// [<see cref="MinScale"/>, <see cref="MaxScale"/>], with values within
        /// <see cref="UnitySnap"/> of 1 snapped to exactly 1.</summary>
        internal static float ResolveScale(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw <= 0f) return 1f;
            if (Math.Abs(raw - 1f) < UnitySnap) return 1f;
            if (raw < MinScale) return MinScale;
            if (raw > MaxScale) return MaxScale;
            return raw;
        }

        /// <summary>A physical screen length in scaled-root units.</summary>
        internal static float ToScaled(float physical, float scale)
        {
            return scale > 0f ? physical / scale : physical;
        }

        /// <summary>A physical GUI-space point (top-left origin) in scaled-root units.</summary>
        internal static Vector2 UnscalePoint(Vector2 physical, float scale)
        {
            return scale > 0f ? physical / scale : physical;
        }

        /// <summary>
        /// Moves a window fully onto a screen of the given size when it fits there, or to
        /// the left / top edge when it is wider / taller than the screen. A window already
        /// on screen is returned unchanged. Used once per window after the scale changes,
        /// because a larger scale shrinks the screen in scaled-root units and can leave a
        /// window the player parked near the right or bottom edge out of reach.
        /// </summary>
        internal static Rect PullOntoScreen(Rect rect, float screenWidth, float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f) return rect;
            float maxX = screenWidth - rect.width;
            float x = rect.x;
            if (x > maxX) x = maxX;
            if (x < 0f) x = 0f;
            float maxY = screenHeight - rect.height;
            float y = rect.y;
            if (y > maxY) y = maxY;
            if (y < 0f) y = 0f;
            return new Rect(x, y, rect.width, rect.height);
        }

        internal enum LockAction { Keep, Set, Release }

        /// <summary>
        /// The scaled-window lock decision, mirroring ClickThroughBlocker's default
        /// focus-follows-mouse rule: take the lock when the pointer is over the window and
        /// the right button (camera drag) is not held, free it when the pointer is
        /// elsewhere, and leave it as it is while the right button is held over the window.
        /// </summary>
        internal static LockAction DecideLock(bool held, bool mouseOver, bool rightButtonHeld)
        {
            if (mouseOver)
            {
                if (rightButtonHeld) return LockAction.Keep;
                return held ? LockAction.Keep : LockAction.Set;
            }
            return held ? LockAction.Release : LockAction.Keep;
        }

        /// <summary>A held scaled-window lock is stale once its window has not been drawn
        /// for more than one whole frame (the window closed, or its host stopped drawing).</summary>
        internal static bool IsLockStale(int lastDrawFrame, int currentFrame)
        {
            return lastDrawFrame < currentFrame - 1;
        }

        /// <summary>The lock hit test runs on Repaint only, when the returned rect is the
        /// resolved one; any other event (Layout above all) is skipped.</summary>
        internal static bool ShouldEvaluateLock(bool hasEvent, bool isRepaint)
        {
            return hasEvent && isRepaint;
        }

        /// <summary>Set / pointer-left release transitions taken by the scaled path, for the
        /// in-game no-flap check.</summary>
        internal static int LockTransitionCount { get; private set; }

        internal static string ScaledLockId(int windowId)
        {
            return ScaledLockPrefix + windowId.ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatScaleChangeLog(float previous, float current, float raw,
            string host)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "UI scale changed {0:0.###} -> {1:0.###}: Parsek windows, popups and tooltips"
                + " now drawn at {1:0.###}x (GameSettings.UI_SCALE={2:0.###}{3}, host={4})",
                previous, current, raw, OverrideForTesting.HasValue ? ", test override" : "",
                host ?? "?");
        }

        // ----- live -----

        /// <summary>The scale Parsek should draw at right now.</summary>
        internal static float CurrentSettingScale()
        {
            if (OverrideForTesting.HasValue) return ResolveScale(OverrideForTesting.Value);
            return ResolveScale(ReadRawSetting());
        }

        /// <summary>The raw stock setting, or 1 when it cannot be read (headless tests).</summary>
        internal static float ReadRawSetting()
        {
            if (OverrideForTesting.HasValue) return OverrideForTesting.Value;
            try
            {
                return ReadGameSettingsUiScaleCore();
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadGameSettingsUiScaleCore()
        {
            return GameSettings.UI_SCALE;
        }

        /// <summary>The screen width in the units of the innermost scope.</summary>
        internal static float ScaledScreenWidth => ToScaled(Screen.width, ActiveScale);

        /// <summary>The screen height in the units of the innermost scope.</summary>
        internal static float ScaledScreenHeight => ToScaled(Screen.height, ActiveScale);

        /// <summary>
        /// A point in the current GUI space (window-local inside a window callback) in the
        /// scaled-root units the host scope draws popups in. <c>GUIToScreenPoint</c>
        /// applies <c>GUI.matrix</c> and so returns physical pixels; dividing by the
        /// matrix's own scale brings the point back. The LIVE matrix is read rather than
        /// <see cref="ActiveScale"/> because Unity runs window callbacks after the host's
        /// OnGUI code has returned (the scope is already closed there) and restores each
        /// window's own matrix around its callback. At scale 1 this is exactly
        /// <c>GUIUtility.GUIToScreenPoint</c>.
        /// </summary>
        internal static Vector2 GuiPointToScaledRoot(Vector2 guiPoint)
        {
            return UnscalePoint(GUIUtility.GUIToScreenPoint(guiPoint), GUI.matrix.m00);
        }

        /// <summary>
        /// Opens a host scope at the current setting. Dispose restores the previous
        /// <c>GUI.matrix</c> and <see cref="ActiveScale"/>. Logs once per host each time
        /// that host's scale changes (the first draw at a scale other than 1 included).
        /// </summary>
        internal static HostScope BeginHost(string host)
        {
            return BeginHost(host, CurrentSettingScale());
        }

        internal static HostScope BeginHost(string host, float scale)
        {
            float s = ResolveScale(scale);
            string key = host ?? "?";
            float last;
            if (!lastLoggedScaleByHost.TryGetValue(key, out last)) last = 1f;
            if (Math.Abs(s - last) > UnitySnap)
            {
                ParsekLog.Info(Tag, FormatScaleChangeLog(last, s, ReadRawSetting(), key));
                lastLoggedScaleByHost[key] = s;
            }
            return new HostScope(s);
        }

        internal struct HostScope : IDisposable
        {
            private readonly bool applied;
            private readonly Matrix4x4 previousMatrix;
            private readonly float previousScale;

            internal HostScope(float scale)
            {
                previousScale = ActiveScale;
                previousMatrix = default(Matrix4x4);
                applied = false;
                if (scale != 1f)
                {
                    previousMatrix = GUI.matrix;
                    GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                    applied = true;
                }
                ActiveScale = scale;
            }

            public void Dispose()
            {
                if (applied)
                    GUI.matrix = previousMatrix;
                ActiveScale = previousScale;
            }
        }

        /// <summary>
        /// The one window call every Parsek window uses. At scale 1 it is exactly
        /// <c>ClickThruBlocker.GUILayoutWindow</c>. Under any other scale it draws with
        /// <c>GUILayout.Window</c> and holds a Parsek-owned <c>ALLBUTCAMERAS</c> lock while
        /// the pointer is over the window (see the class summary for why). Either way, the
        /// first draw after the scale changed pulls the window back onto the screen.
        /// </summary>
        internal static Rect Window(int id, Rect rect, GUI.WindowFunction func, string title,
            GUIStyle style, params GUILayoutOption[] options)
        {
            float scale = ActiveScale;
            Rect drawn;
            if (scale == 1f)
            {
                ReleaseScaledLock(id, "scale-1");
                drawn = ClickThruBlocker.GUILayoutWindow(id, rect, func, title, style, options);
            }
            else
            {
                drawn = GUILayout.Window(id, rect, func, title, style, options);
                UpdateScaledLock(id, title, drawn);
            }
            return PullOnScreenAfterScaleChange(id, title, drawn, scale);
        }

        private static Rect PullOnScreenAfterScaleChange(int id, string title, Rect drawn,
            float scale)
        {
            float last;
            if (!lastDrawScaleByWindow.TryGetValue(id, out last)) last = 1f;
            if (last == scale) return drawn;
            if (Event.current != null && Event.current.type != EventType.Repaint)
                return drawn; // the resolved size is final on Repaint
            lastDrawScaleByWindow[id] = scale;
            Rect pulled = PullOntoScreen(drawn, ScaledScreenWidth, ScaledScreenHeight);
            if (pulled.x != drawn.x || pulled.y != drawn.y)
            {
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Window '{0}' pulled onto screen after UI scale change {1:0.###} -> {2:0.###}:"
                    + " ({3:0},{4:0}) -> ({5:0},{6:0}) size {7:0}x{8:0} screen {9:0}x{10:0}",
                    title, last, scale, drawn.x, drawn.y, pulled.x, pulled.y,
                    drawn.width, drawn.height, ScaledScreenWidth, ScaledScreenHeight));
            }
            return pulled;
        }

        private static void UpdateScaledLock(int id, string title, Rect drawn)
        {
            ScaledLockState state;
            if (!scaledLocks.TryGetValue(id, out state))
            {
                state = new ScaledLockState();
                scaledLocks[id] = state;
            }
            state.LastDrawFrame = Time.frameCount;
            state.Title = title;
            Event e = Event.current;
            // Repaint only: a Layout pass returns the rect it was handed, and a
            // content-sized window (the main window zeroes its height before every draw)
            // hands a zero-height rect, so a Layout hit test would release the lock and the
            // next Repaint would set it again, every frame.
            if (!ShouldEvaluateLock(e != null, e != null && e.type == EventType.Repaint)) return;
            bool over = drawn.Contains(e.mousePosition);
            bool rightHeld = Input.GetMouseButton(1);
            switch (DecideLock(state.Held, over, rightHeld))
            {
                case LockAction.Set:
                    InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, ScaledLockId(id));
                    state.Held = true;
                    LockTransitionCount++;
                    ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                        "Scaled window lock set: '{0}' id={1} scale={2:0.###}", title, id, ActiveScale));
                    break;
                case LockAction.Release:
                    InputLockManager.RemoveControlLock(ScaledLockId(id));
                    state.Held = false;
                    LockTransitionCount++;
                    ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                        "Scaled window lock released: '{0}' id={1} reason=pointer-left", title, id));
                    break;
            }
        }

        private static void ReleaseScaledLock(int id, string reason)
        {
            ScaledLockState state;
            if (!scaledLocks.TryGetValue(id, out state) || !state.Held) return;
            InputLockManager.RemoveControlLock(ScaledLockId(id));
            state.Held = false;
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Scaled window lock released: '{0}' id={1} reason={2}", state.Title, id, reason));
        }

        /// <summary>Whether Parsek holds the scaled-window lock for this window id.</summary>
        internal static bool IsScaledLockHeld(int id)
        {
            ScaledLockState state;
            return scaledLocks.TryGetValue(id, out state) && state.Held;
        }

        /// <summary>
        /// Frees every scaled-window lock whose window was not drawn last frame. Each draw
        /// host calls it at the top of its OnGUI, before any early return, so a window that
        /// closed (or a host that stopped drawing) never leaves the game input locked.
        /// </summary>
        internal static void SweepStaleLocks()
        {
            if (scaledLocks.Count == 0) return;
            int frame = Time.frameCount;
            List<int> stale = null;
            foreach (var kv in scaledLocks)
            {
                if (kv.Value.Held && IsLockStale(kv.Value.LastDrawFrame, frame))
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(kv.Key);
                }
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
                ReleaseScaledLock(stale[i], "window-not-drawn");
        }

        /// <summary>Frees every scaled-window lock; called when a draw host is destroyed.</summary>
        internal static void ReleaseAllScaledLocks(string reason)
        {
            if (scaledLocks.Count == 0) return;
            var ids = new List<int>(scaledLocks.Keys);
            int released = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (IsScaledLockHeld(ids[i])) released++;
                ReleaseScaledLock(ids[i], reason);
            }
            if (released > 0)
                ParsekLog.Verbose(Tag, "Released " + released.ToString(CultureInfo.InvariantCulture)
                    + " scaled window lock(s): " + reason);
        }

        internal static void ResetForTesting()
        {
            OverrideForTesting = null;
            ActiveScale = 1f;
            lastLoggedScaleByHost.Clear();
            scaledLocks.Clear();
            lastDrawScaleByWindow.Clear();
            LockTransitionCount = 0;
        }
    }
}
