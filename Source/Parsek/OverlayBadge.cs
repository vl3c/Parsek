using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Parsek
{
    internal sealed class OverlayBadge : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const float TooltipWidth = 320f;
        private const float TooltipHeight = 54f;

        private string screen;
        private string itemName;
        private string tooltip;
        private Transform watchedParent;
        private bool hovered;
        private GUIStyle tooltipStyle;

        internal void Configure(string screenName, string name, string tooltipText, Color tint)
        {
            screen = screenName ?? "StockUiOverlay";
            itemName = name ?? "";
            tooltip = tooltipText ?? "";
            watchedParent = transform.parent;

            var rect = gameObject.GetComponent<RectTransform>();
            if (rect == null)
                rect = gameObject.AddComponent<RectTransform>();

            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(18f, 18f);
            rect.anchoredPosition = new Vector2(-6f, -6f);

            var image = gameObject.GetComponent<RawImage>();
            if (image == null)
                image = gameObject.AddComponent<RawImage>();

            image.texture = ResolveBadgeTexture();
            image.color = tint;
            image.raycastTarget = true;
        }

        private static Texture ResolveBadgeTexture()
        {
            try
            {
                var db = GameDatabase.Instance;
                if (db != null)
                {
                    var texture = db.GetTexture("Parsek/Textures/clock_overlay", false);
                    if (texture != null)
                        return texture;
                }
            }
            catch
            {
                // Fall back to a tinted square if GameDatabase is unavailable.
            }

            return Texture2D.whiteTexture;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
        }

        private void Update()
        {
            if (watchedParent != null && transform.parent != null)
                return;

            ParsekLog.Verbose("StockUiOverlay",
                $"StockUiOverlay: {screen} badge self-destruct, parent gone — name={itemName}");
            Destroy(gameObject);
        }

        private void OnGUI()
        {
            if (!hovered || string.IsNullOrEmpty(tooltip))
                return;

            if (tooltipStyle == null)
            {
                tooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true,
                    padding = new RectOffset(10, 10, 8, 8)
                };
            }

            Vector3 mouse = Input.mousePosition;
            float x = Mathf.Min(mouse.x + 16f, Screen.width - TooltipWidth - 8f);
            float y = Mathf.Min(Screen.height - mouse.y + 16f, Screen.height - TooltipHeight - 8f);
            GUI.Box(new Rect(x, y, TooltipWidth, TooltipHeight), tooltip, tooltipStyle);
        }
    }
}
