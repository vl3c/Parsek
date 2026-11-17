using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The frame a read-only table window draws inside: first-open placement, the resize
    /// clamp and handle, the opaque window style with the GUI colours reset around it, the
    /// UI-scaled window call keyed by the window-id key's hash, the position log, the camera
    /// input lock while the pointer is over the window, and the footer (the hover strip, then
    /// the full-width Close, then the resize handle and the drag area).
    /// <para>The owner keeps its open state and its body: it calls <see cref="Draw"/> only
    /// while open, passes the body drawer, and is told about a Close click through the
    /// callback it handed the constructor. The owner also constructs the
    /// <see cref="TooltipEchoBox"/>, so the strip height stays declared in the window's own
    /// file, where <c>TooltipEchoBudgetTests</c> reads it.</para>
    /// </summary>
    internal sealed class TableWindowChrome
    {
        private readonly ParsekUI parentUI;
        private readonly string windowIdKey;
        private readonly string inputLockId;
        // The name ParsekUI.LogWindowPosition logs moves under.
        private readonly string positionLogName;
        // The name the resize clamp and the resize handle log under.
        private readonly string resizeLogName;
        private readonly float minWidth;
        private readonly float minHeight;
        private readonly Func<Rect, Rect> firstOpenRect;
        private readonly TooltipEchoBox tooltipEcho;
        private readonly Action onClose;
        private readonly GUI.WindowFunction windowFunction;

        private Rect windowRect;
        private Rect lastWindowRect;
        private bool isResizing;
        private bool hasInputLock;
        // The owner's body drawer from the last Draw. Kept in a field rather than cleared
        // after the window call: IMGUI may run the window function after Draw returns.
        private Action drawBody;

        internal TableWindowChrome(
            ParsekUI parentUI,
            string windowIdKey,
            string inputLockId,
            string positionLogName,
            string resizeLogName,
            float minWidth,
            float minHeight,
            Func<Rect, Rect> firstOpenRect,
            TooltipEchoBox tooltipEcho,
            Action onClose)
        {
            this.parentUI = parentUI;
            this.windowIdKey = windowIdKey;
            this.inputLockId = inputLockId;
            this.positionLogName = positionLogName;
            this.resizeLogName = resizeLogName;
            this.minWidth = minWidth;
            this.minHeight = minHeight;
            this.firstOpenRect = firstOpenRect;
            this.tooltipEcho = tooltipEcho;
            this.onClose = onClose;
            windowFunction = DrawWindow;
        }

        /// <summary>
        /// The live window rect. Writable from outside the draw pass (the owner's
        /// <c>WindowRectForTesting</c>): a commanded rect of width 1 or more suppresses the
        /// first-open placement rather than being overwritten by it.
        /// </summary>
        internal Rect WindowRect
        {
            get { return windowRect; }
            set { windowRect = value; }
        }

        /// <summary>A rect narrower than one unit has never been placed. Pure.</summary>
        internal static bool NeedsFirstOpenPlacement(Rect rect)
        {
            return rect.width < 1f;
        }

        /// <summary>What the camera input lock does after a draw.</summary>
        internal enum InputLockStep
        {
            /// <summary>Leave the lock as it is.</summary>
            Keep,
            /// <summary>Take the lock: the pointer is over the window and no lock is held.</summary>
            Acquire,
            /// <summary>Drop the lock: the pointer left the window while the lock is held.</summary>
            Release,
        }

        /// <summary>
        /// The input lock decision after a draw: held exactly while the pointer is over the
        /// window. Pure.
        /// </summary>
        internal static InputLockStep DecideInputLock(bool hasLock, bool pointerInside)
        {
            if (pointerInside)
                return hasLock ? InputLockStep.Keep : InputLockStep.Acquire;
            return hasLock ? InputLockStep.Release : InputLockStep.Keep;
        }

        /// <summary>
        /// Draws the window: places it on first open beside <paramref name="mainWindowRect"/>,
        /// applies the resize clamp, draws <paramref name="drawBody"/> and the footer inside
        /// the scaled window, logs a move, and takes or drops the input lock. The owner calls
        /// it only while open.
        /// </summary>
        internal void Draw(Rect mainWindowRect, string title, Action drawBody)
        {
            if (NeedsFirstOpenPlacement(windowRect))
                windowRect = firstOpenRect(mainWindowRect);

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                minWidth, minHeight, resizeLogName);

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            this.drawBody = drawBody;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ParsekUiScale.Window(
                    windowIdKey.GetHashCode(),
                    windowRect,
                    windowFunction,
                    title,
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition(positionLogName, ref lastWindowRect, windowRect);

            switch (DecideInputLock(hasInputLock, windowRect.Contains(Event.current.mousePosition)))
            {
                case InputLockStep.Acquire:
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, inputLockId);
                    hasInputLock = true;
                    break;
                case InputLockStep.Release:
                    ReleaseInputLock();
                    break;
            }
        }

        /// <summary>Drops the camera input lock if this window holds it.</summary>
        internal void ReleaseInputLock()
        {
            if (!hasInputLock) return;
            InputLockManager.RemoveControlLock(inputLockId);
            hasInputLock = false;
        }

        private void DrawWindow(int windowID)
        {
            drawBody?.Invoke();

            // Hover-help strip, drawn after the body so the live GUI.tooltip read sees a
            // hovered cell, and directly above Close (the house ordering).
            tooltipEcho.Draw();

            // Full-width Close, matching the other Parsek windows.
            if (GUILayout.Button("Close"))
                onClose();

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, resizeLogName);
            GUI.DragWindow();
        }
    }
}
