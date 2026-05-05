using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Parsek
{
    internal sealed class OverlayBadge : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const string TexturePath = "Squad/Alarms/Icons/default";
        private const float TooltipWidth = 320f;
        private const float TooltipHeight = 54f;

        private static bool textureMissingLogged;
        private static bool gameDatabaseUnavailableLogged;
        private static bool textureExceptionLogged;

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
                    var texture = db.GetTexture(TexturePath, false);
                    if (texture != null)
                        return texture;

                    LogTextureMissingOnce(
                        $"OverlayBadge: texture '{TexturePath}' not found; using tinted fallback");
                }
                else
                {
                    LogGameDatabaseUnavailableOnce("OverlayBadge: GameDatabase unavailable; using tinted fallback");
                }
            }
            catch (System.Exception ex)
            {
                if (!textureExceptionLogged)
                {
                    textureExceptionLogged = true;
                    ParsekLog.VerboseRateLimited(
                        "StockUiOverlay",
                        "overlay-badge-texture-exception",
                        $"OverlayBadge: texture lookup failed for '{TexturePath}'; using tinted fallback ({ex.Message})");
                }
            }

            return Texture2D.whiteTexture;
        }

        private static void LogTextureMissingOnce(string message)
        {
            if (textureMissingLogged)
                return;

            textureMissingLogged = true;
            ParsekLog.Verbose("StockUiOverlay", message);
        }

        private static void LogGameDatabaseUnavailableOnce(string message)
        {
            if (gameDatabaseUnavailableLogged)
                return;

            gameDatabaseUnavailableLogged = true;
            ParsekLog.Verbose("StockUiOverlay", message);
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
