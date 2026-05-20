using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// Adds a hover tooltip to the stock funds and science currency widgets explaining
    /// how Parsek's committed-future reservation produces the displayed value.
    ///
    /// The stock funds / science bars are patched by <see cref="KspStatePatcher"/> to the
    /// ledger's AVAILABLE value (current balance minus committed-future spend), so the
    /// number on screen is already Total - Reserved. The tooltip surfaces the Total and
    /// Reserved components so the player understands where the displayed number comes from.
    ///
    /// Reputation is intentionally NOT decorated: <see cref="KspStatePatcher.PatchReputation"/>
    /// writes the running value, not an available value, so the reputation bar shows the
    /// true current reputation and a "Total - Reserved" framing would be false.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class CurrencyReservationOverlay : MonoBehaviour
    {
        private const string Tag = "CurrencyOverlay";
        internal const string OverlayName = "Parsek_CurrencyTooltip";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Reserved amounts below these thresholds are rounding noise; hide the tooltip.
        private const double FundsEpsilon = 0.5;
        private const double ScienceEpsilon = 0.05;

        private bool active;

        private void Start()
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.SPACECENTER && scene != GameScenes.FLIGHT)
            {
                ParsekLog.Verbose(Tag,
                    $"CurrencyOverlay: scene={scene} has no currency bar - controller idle");
                return;
            }

            active = true;
            ParsekLog.Info(Tag,
                $"CurrencyOverlay: initialised for scene={scene} - starting attach loop");
            StartCoroutine(AttachLoop());
        }

        private void OnDestroy()
        {
            active = false;
        }

        // The currency app shows/hides dynamically and its widgets are instantiated a few
        // frames after scene load, so re-check on a slow heartbeat for the scene lifetime.
        private IEnumerator AttachLoop()
        {
            var wait = new WaitForSeconds(1f);
            while (active)
            {
                if (StockUiOverlayController.ShouldApplyOverlays())
                    EnsureTooltipsAttached();
                else
                    StripAllTooltips();
                yield return wait;
            }
        }

        /// <summary>
        /// Finds the active funds / science widgets and attaches a hover tooltip to each
        /// if not already present. Returns the number of widgets newly decorated.
        /// </summary>
        internal static int EnsureTooltipsAttached()
        {
            int attached = 0;

            FundsWidget funds = UnityEngine.Object.FindObjectOfType<FundsWidget>();
            if (funds != null && EnsureTooltip(funds.transform, GetFundsTooltip))
            {
                attached++;
                ParsekLog.Verbose(Tag, "CurrencyOverlay: attached tooltip to Funds widget");
            }

            ScienceWidget science = UnityEngine.Object.FindObjectOfType<ScienceWidget>();
            if (science != null && EnsureTooltip(science.transform, GetScienceTooltip))
            {
                attached++;
                ParsekLog.Verbose(Tag, "CurrencyOverlay: attached tooltip to Science widget");
            }

            return attached;
        }

        /// <summary>
        /// Removes any attached currency tooltips. Returns the number stripped.
        /// </summary>
        internal static int StripAllTooltips()
        {
            int stripped = 0;
            FundsWidget funds = UnityEngine.Object.FindObjectOfType<FundsWidget>();
            stripped += StripTooltip(funds != null ? funds.transform : null);
            ScienceWidget science = UnityEngine.Object.FindObjectOfType<ScienceWidget>();
            stripped += StripTooltip(science != null ? science.transform : null);

            if (stripped > 0)
                ParsekLog.Verbose(Tag,
                    $"CurrencyOverlay: feature disabled - stripped overlayCount={stripped}");
            return stripped;
        }

        // Attaches a full-rect transparent hover area + tooltip to the widget. Returns true
        // when a new overlay was created, false when one already existed or widget was null.
        internal static bool EnsureTooltip(Transform widget, Func<string> provider)
        {
            if (widget == null)
                return false;
            if (widget.Find(OverlayName) != null)
                return false;

            var go = new GameObject(OverlayName);
            go.transform.SetParent(widget, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = go.AddComponent<RawImage>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;

            var tip = go.AddComponent<CurrencyReservationTooltip>();
            tip.Configure(provider);
            return true;
        }

        private static int StripTooltip(Transform widget)
        {
            if (widget == null)
                return 0;
            Transform existing = widget.Find(OverlayName);
            if (existing == null)
                return 0;
            UnityEngine.Object.Destroy(existing.gameObject);
            return 1;
        }

        // ================================================================
        // Tooltip text (pure / testable)
        // ================================================================

        /// <summary>
        /// Builds the "Total / Reserved" tooltip body, or null when the reserved amount is
        /// at or below <paramref name="epsilon"/> (nothing committed - no tooltip).
        /// <paramref name="available"/> is the value shown on the bar, so by construction
        /// the bar equals <paramref name="available"/> = Total - Reserved.
        /// </summary>
        internal static string BuildReservationTooltip(
            string label, double total, double available, string format, double epsilon)
        {
            double reserved = total - available;
            if (reserved <= epsilon)
                return null;

            return label + "\n"
                + "Total: " + total.ToString(format, IC) + "\n"
                + "Reserved: " + reserved.ToString(format, IC);
        }

        // "Available" is anchored on the LIVE singleton (Funding.Instance.Funds /
        // ResearchAndDevelopment.Instance.Science), which IS the number drawn on the bar.
        // Reserved is the ledger's committed-future drawdown (current balance minus the
        // ledger available), and Total = displayed + Reserved. Anchoring on the live value
        // rather than GetAvailable*() keeps Total - Reserved exactly equal to the on-screen
        // number even when KspStatePatcher applies an extra hold-back to the bar (science
        // pending-tech-unlock catch-up window) that GetAvailable*() does not reflect.
        internal static string GetFundsTooltip()
        {
            var funds = LedgerOrchestrator.Funds;
            if (funds == null || Funding.Instance == null)
                return null;
            double reserved = funds.GetProjectionCurrentBalance() - funds.GetAvailableFunds();
            if (reserved < 0.0)
                reserved = 0.0;
            double displayed = Funding.Instance.Funds;
            return BuildReservationTooltip("Funds", displayed + reserved, displayed, "N0", FundsEpsilon);
        }

        internal static string GetScienceTooltip()
        {
            var science = LedgerOrchestrator.Science;
            if (science == null || ResearchAndDevelopment.Instance == null)
                return null;
            double reserved = science.GetProjectionCurrentBalance() - science.GetAvailableScience();
            if (reserved < 0.0)
                reserved = 0.0;
            double displayed = ResearchAndDevelopment.Instance.Science;
            return BuildReservationTooltip("Science", displayed + reserved, displayed, "F1", ScienceEpsilon);
        }
    }

    /// <summary>
    /// Hover area that draws a dynamically-computed tooltip near the cursor. The tooltip
    /// text is recomputed on every display (reservation drifts as the timeline advances);
    /// a null/empty result suppresses the box, which is how "show only when reserved" is
    /// realised without attaching / detaching the overlay.
    ///
    /// Unlike <see cref="OverlayBadge"/> this has no self-destruct-on-reparent guard: the
    /// stock currency widgets are instantiated once and are not list-virtualised / recycled,
    /// so the parent is stable for the scene lifetime and Unity destroys this child with it.
    /// </summary>
    internal sealed class CurrencyReservationTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const float TooltipWidth = 220f;

        private Func<string> provider;
        private bool hovered;
        private GUIStyle style;

        internal void Configure(Func<string> tooltipProvider)
        {
            provider = tooltipProvider;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
        }

        private void OnGUI()
        {
            if (!hovered || provider == null)
                return;

            string text = provider();
            if (string.IsNullOrEmpty(text))
                return;

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    padding = new RectOffset(10, 10, 8, 8)
                };
            }

            var content = new GUIContent(text);
            float height = style.CalcHeight(content, TooltipWidth);
            Vector3 mouse = Input.mousePosition;
            float x = Mathf.Min(mouse.x + 16f, Screen.width - TooltipWidth - 8f);
            float y = Mathf.Min(Screen.height - mouse.y + 16f, Screen.height - height - 8f);
            GUI.Box(new Rect(x, y, TooltipWidth, height), content, style);
        }
    }
}
