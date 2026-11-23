using System;
using System.Collections.Generic;
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

        // ------------------------------------------------------------------ shared pure helpers

        /// <summary>The empty-state line before any target was given (the bare seam open).</summary>
        internal const string EmptyNoTargetText = "Nothing to show.";

        /// <summary>A window title from a fixed part and a target name. Pure.</summary>
        internal static string BuildWindowTitle(string prefix, string targetName)
        {
            return string.IsNullOrEmpty(targetName)
                ? prefix
                : prefix + ": " + targetName;
        }

        /// <summary>First-open rect: right of the main window, top-aligned with it, moved
        /// <paramref name="cascadeOffset"/> down and right. Pure.</summary>
        internal static Rect DefaultRect(Rect mainWindowRect, float cascadeOffset, float width, float height)
        {
            return new Rect(mainWindowRect.x + mainWindowRect.width + 10 + cascadeOffset,
                mainWindowRect.y + cascadeOffset, width, height);
        }

        /// <summary>
        /// The mission change signature both windows rebuild on: the committed-recordings
        /// version, whether the mission still resolves, its name, its excluded interval keys as
        /// an order-independent set hash, and <see cref="DisplayNames.Version"/> (a vessel rename
        /// moves no StateVersion). Pure; an in-memory compare value, never persisted.
        /// </summary>
        internal static int ComputeChangeSignature(
            int stateVersion, bool missionFound, string missionName,
            ICollection<string> excludedIntervalKeys, int displayNamesVersion)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + stateVersion;
                h = h * 31 + displayNamesVersion;
                h = h * 31 + (missionFound ? 1 : 0);
                h = h * 31 + (missionName != null ? StringComparer.Ordinal.GetHashCode(missionName) : 0);
                int setHash = 0;
                int count = 0;
                if (excludedIntervalKeys != null)
                {
                    foreach (string key in excludedIntervalKeys)
                    {
                        // Summed, so the order a HashSet enumerates in does not matter.
                        int k = key != null ? StringComparer.Ordinal.GetHashCode(key) : 0;
                        setHash += (k * 0x2F0B3A49) ^ (k >> 7);
                        count++;
                    }
                }
                h = h * 31 + count;
                h = h * 31 + setHash;
                return h;
            }
        }
    }
}
