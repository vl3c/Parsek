using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared delayed IMGUI tooltip bubble renderer for Parsek windows.
    /// </summary>
    internal static class TooltipBubble
    {
        internal const float HoverDelaySeconds = 1.0f;

        private const float CursorOffsetX = 16f;
        private const float CursorOffsetY = 18f;
        private const float WindowPadding = 6f;
        private const float BubblePaddingX = 8f;
        private const float BubblePaddingY = 6f;
        private const float MaxTextWidth = 320f;
        private const float MinTextWidth = 120f;
        private const float MinimumUsableTextWidth = 48f;

        private sealed class HoverState
        {
            internal string Text = string.Empty;
            internal float HoverStartRealtime;
        }

        private static readonly Dictionary<string, HoverState> hoverStates =
            new Dictionary<string, HoverState>();

        private static GUIStyle bubbleLabelStyle;
        private static GUIStyle bubbleBoxStyle;

        internal static void DrawForWindow(string scopeKey, Rect windowRect, string overrideTooltip = null)
        {
            Event current = Event.current;
            if (current == null || string.IsNullOrEmpty(scopeKey))
                return;

            string tooltip = string.IsNullOrEmpty(overrideTooltip)
                ? (GUI.tooltip ?? string.Empty)
                : overrideTooltip;

            Rect lastLayoutRect = current.type == EventType.Repaint
                ? GUILayoutUtility.GetLastRect()
                : default(Rect);
            Rect localBounds = ComputeLocalBounds(windowRect, lastLayoutRect);
            DrawForWindow(
                scopeKey,
                localBounds,
                current.mousePosition,
                tooltip,
                Time.realtimeSinceStartup,
                current.type);
        }

        internal static void ResetForTesting()
        {
            hoverStates.Clear();
        }

        internal static bool ShouldShow(string tooltip, float hoverStartRealtime, float nowRealtime)
        {
            return !string.IsNullOrEmpty(tooltip)
                && nowRealtime - hoverStartRealtime >= HoverDelaySeconds;
        }

        internal static bool ShouldResetHover(string previousTooltip, string currentTooltip, bool pointerInWindow)
        {
            return !pointerInWindow
                || string.IsNullOrEmpty(currentTooltip)
                || !string.Equals(previousTooltip, currentTooltip, StringComparison.Ordinal);
        }

        internal static float ComputeTextWidth(float naturalWidth, float windowWidth)
        {
            float maxWidth = Mathf.Max(
                MinimumUsableTextWidth,
                Mathf.Min(MaxTextWidth, windowWidth - (WindowPadding * 2f) - (BubblePaddingX * 2f)));
            float minWidth = Mathf.Min(MinTextWidth, maxWidth);
            return Mathf.Clamp(naturalWidth, minWidth, maxWidth);
        }

        internal static Rect ComputeBubbleRect(Vector2 mousePosition, Vector2 bubbleSize, Rect bounds)
        {
            float minX = bounds.xMin + WindowPadding;
            float maxX = bounds.xMax - bubbleSize.x - WindowPadding;
            float x = mousePosition.x + CursorOffsetX;
            if (x > maxX)
                x = mousePosition.x - bubbleSize.x - CursorOffsetX;
            x = maxX < minX ? minX : Mathf.Clamp(x, minX, maxX);

            float minY = bounds.yMin + WindowPadding;
            float maxY = bounds.yMax - bubbleSize.y - WindowPadding;
            float y = mousePosition.y + CursorOffsetY;
            if (y > maxY)
                y = mousePosition.y - bubbleSize.y - CursorOffsetY;
            y = maxY < minY ? minY : Mathf.Clamp(y, minY, maxY);

            return new Rect(x, y, bubbleSize.x, bubbleSize.y);
        }

        internal static Rect ComputeLocalBounds(Rect windowRect, Rect lastLayoutRect)
        {
            float height = windowRect.height;
            if (height <= 0f && lastLayoutRect.height > 0f)
                height = lastLayoutRect.yMax + WindowPadding;

            return new Rect(
                0f,
                0f,
                Mathf.Max(0f, windowRect.width),
                Mathf.Max(0f, height));
        }

        private static void DrawForWindow(
            string scopeKey,
            Rect localBounds,
            Vector2 mousePosition,
            string tooltip,
            float nowRealtime,
            EventType eventType)
        {
            if (eventType != EventType.Repaint)
                return;

            bool pointerInWindow = localBounds.Contains(mousePosition);
            HoverState state;
            hoverStates.TryGetValue(scopeKey, out state);

            if (state == null)
            {
                state = new HoverState();
                hoverStates[scopeKey] = state;
            }

            if (ShouldResetHover(state.Text, tooltip, pointerInWindow))
            {
                state.Text = pointerInWindow && !string.IsNullOrEmpty(tooltip)
                    ? tooltip
                    : string.Empty;
                state.HoverStartRealtime = nowRealtime;
                return;
            }

            if (!ShouldShow(tooltip, state.HoverStartRealtime, nowRealtime))
                return;

            EnsureStyles();
            DrawBubble(tooltip, mousePosition, localBounds);
        }

        private static void EnsureStyles()
        {
            if (bubbleLabelStyle == null)
            {
                bubbleLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    fontSize = 11,
                    alignment = TextAnchor.UpperLeft
                };
                bubbleLabelStyle.normal.textColor = Color.white;
                bubbleLabelStyle.margin = new RectOffset(0, 0, 0, 0);
                bubbleLabelStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            if (bubbleBoxStyle == null)
            {
                bubbleBoxStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft
                };
            }
        }

        private static void DrawBubble(string tooltip, Vector2 mousePosition, Rect localBounds)
        {
            GUIContent content = new GUIContent(tooltip);
            float naturalWidth = bubbleLabelStyle.CalcSize(content).x;
            float textWidth = ComputeTextWidth(naturalWidth, localBounds.width);
            float textHeight = bubbleLabelStyle.CalcHeight(content, textWidth);
            Vector2 bubbleSize = new Vector2(
                textWidth + BubblePaddingX * 2f,
                textHeight + BubblePaddingY * 2f);
            Rect bubbleRect = ComputeBubbleRect(mousePosition, bubbleSize, localBounds);
            Rect labelRect = new Rect(
                bubbleRect.x + BubblePaddingX,
                bubbleRect.y + BubblePaddingY,
                textWidth,
                textHeight);

            Color previousColor = GUI.color;
            Color previousBackground = GUI.backgroundColor;
            Color previousContent = GUI.contentColor;
            try
            {
                GUI.color = Color.white;
                GUI.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 0.96f);
                GUI.contentColor = Color.white;
                GUI.Box(bubbleRect, GUIContent.none, bubbleBoxStyle);
                GUI.Label(labelRect, content, bubbleLabelStyle);
            }
            finally
            {
                GUI.color = previousColor;
                GUI.backgroundColor = previousBackground;
                GUI.contentColor = previousContent;
            }
        }
    }
}
