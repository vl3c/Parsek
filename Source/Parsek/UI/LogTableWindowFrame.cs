using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The window frame the two log-table windows share (the Mission Log / Route History,
    /// <see cref="StructureListWindowUI"/>, and the Mission Profile,
    /// <see cref="MissionProfileWindowUI"/>): the live rect and its first-open seed, the
    /// resize drag and screen fit, the opaque window style with the house GUI colours,
    /// the UI-scaled window call, the position log and the camera input lock while
    /// hovered. Each window keeps its own body (table, hover strip, Close), so the frame
    /// knows nothing about rows.
    /// </summary>
    internal sealed class LogTableWindowFrame
    {
        private readonly string windowIdKey;
        private readonly string inputLockId;
        private readonly string logName;
        private readonly string positionName;
        private readonly float minWidth;
        private readonly float minHeight;

        private bool hasInputLock;
        private bool isResizing;
        private Rect lastWindowRect;

        /// <summary>The live window rect. Seeded from the caller's default on the first
        /// draw only while <c>width &lt; 1</c>, so a rect written before the first draw (the
        /// seam's <c>op=rect</c>) suppresses the seed rather than being overwritten.</summary>
        internal Rect WindowRect;

        internal LogTableWindowFrame(string windowIdKey, string inputLockId, string logName,
            string positionName, float minWidth, float minHeight)
        {
            this.windowIdKey = windowIdKey;
            this.inputLockId = inputLockId;
            this.logName = logName;
            this.positionName = positionName;
            this.minWidth = minWidth;
            this.minHeight = minHeight;
        }

        internal bool HasInputLock => hasInputLock;

        /// <summary>
        /// Draws the window around <paramref name="drawBody"/>: seeds the rect, fits it,
        /// draws through <see cref="ParsekUiScale.Window"/> with the opaque style, logs a
        /// moved rect and holds the camera lock while the pointer is over it.
        /// </summary>
        internal void Draw(ParsekUI parentUI, Rect defaultRect, string title, GUI.WindowFunction drawBody)
        {
            if (WindowRect.width < 1f)
                WindowRect = defaultRect;

            ParsekUI.HandleResizeDrag(ref WindowRect, ref isResizing, minWidth, minHeight, logName);

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                WindowRect = ParsekUiScale.Window(
                    windowIdKey.GetHashCode(),
                    WindowRect,
                    drawBody,
                    title,
                    opaqueWindowStyle,
                    GUILayout.Width(WindowRect.width),
                    GUILayout.Height(WindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition(positionName, ref lastWindowRect, WindowRect);

            if (WindowRect.Contains(Event.current.mousePosition))
            {
                if (!hasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, inputLockId);
                    hasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        /// <summary>The corner resize handle and the title-bar drag, drawn last inside the
        /// window function.</summary>
        internal void DrawResizeHandleAndDrag()
        {
            ParsekUI.DrawResizeHandle(WindowRect, ref isResizing, logName);
            GUI.DragWindow();
        }

        internal void ReleaseInputLock()
        {
            if (!hasInputLock) return;
            InputLockManager.RemoveControlLock(inputLockId);
            hasInputLock = false;
        }
    }
}
