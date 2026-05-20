using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Adds a hover tooltip to the stock funds and science currency widgets explaining how
    /// Parsek's committed-future reservation produces the displayed value: the bar already
    /// shows Available = Total - Reserved, and the tooltip surfaces the Total and Reserved
    /// components.
    ///
    /// Hover is detected by a screen-space rectangle test in <see cref="OnGUI"/> against each
    /// widget's world rect, NOT via UGUI pointer handlers. The stock funds widget renders its
    /// digits through a rotating 3D <c>KSP.UI.Screens.Tumbler</c> (an odometer reel whose
    /// transform is rotated about X), which tilts the digit RectTransforms out of the screen
    /// plane and breaks UGUI raycasting there - a raycast-target overlay never received pointer
    /// events on funds while flat science text worked. A screen-rect test sidesteps canvas mode,
    /// sorting, masks, CanvasGroups, and the tumbler rotation entirely.
    ///
    /// Reputation is intentionally NOT decorated: it is never reserved
    /// (<c>KspStatePatcher.PatchReputation</c> writes the true running value), so its tooltip
    /// would always read "Reserved: 0". See docs/dev/research/reputation-reservation-not-warranted.md.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class CurrencyReservationOverlay : MonoBehaviour
    {
        private const string Tag = "CurrencyOverlay";
        private const float TooltipWidth = 147f; // 2/3 of the original 220px width
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly Vector3[] CornerBuf = new Vector3[4];

        private bool active;
        private RectTransform fundsRect;
        private RectTransform scienceRect;
        private GUIStyle style;

        private void Start()
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.SPACECENTER && scene != GameScenes.FLIGHT)
            {
                ParsekLog.Verbose(Tag, $"CurrencyOverlay: scene={scene} has no currency bar - controller idle");
                return;
            }

            active = true;
            ParsekLog.Info(Tag, $"CurrencyOverlay: initialised for scene={scene} - starting widget refresh loop");
            StartCoroutine(RefreshLoop());
        }

        private void OnDestroy()
        {
            active = false;
        }

        // The currency app instantiates its widgets a few frames after scene load; re-find on a
        // slow heartbeat for the scene lifetime. References are cached between ticks (cheap).
        private IEnumerator RefreshLoop()
        {
            var wait = new WaitForSeconds(1f);
            while (active)
            {
                if (fundsRect == null)
                {
                    var w = UnityEngine.Object.FindObjectOfType<FundsWidget>();
                    if (w != null)
                    {
                        fundsRect = w.transform as RectTransform;
                        ParsekLog.Verbose(Tag, "CurrencyOverlay: located Funds widget");
                    }
                }
                if (scienceRect == null)
                {
                    var w = UnityEngine.Object.FindObjectOfType<ScienceWidget>();
                    if (w != null)
                    {
                        scienceRect = w.transform as RectTransform;
                        ParsekLog.Verbose(Tag, "CurrencyOverlay: located Science widget");
                    }
                }
                yield return wait;
            }
        }

        private void OnGUI()
        {
            if (!active || !StockUiOverlayController.ShouldApplyOverlays())
                return;

            Vector2 mouse = Input.mousePosition;
            DrawTooltipIfHover(fundsRect, GetFundsTooltip, "funds", mouse);
            DrawTooltipIfHover(scienceRect, GetScienceTooltip, "science", mouse);
        }

        private void DrawTooltipIfHover(RectTransform rt, Func<string> provider, string key, Vector2 mouse)
        {
            if (rt == null)
                return;
            if (!TryGetWidgetScreenRect(rt, out Rect screenRect))
                return;
            if (!screenRect.Contains(mouse))
                return;

            string text = provider();
            if (string.IsNullOrEmpty(text))
                return;

            ParsekLog.VerboseRateLimited(Tag, "hover-" + key,
                $"CurrencyOverlay: hover over {key} - tooltip={text.Replace("\n", " | ")}", 3.0);

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
            float x = Mathf.Min(mouse.x + 16f, Screen.width - TooltipWidth - 8f);
            float y = Mathf.Min(Screen.height - mouse.y + 16f, Screen.height - height - 8f);
            GUI.Box(new Rect(x, y, TooltipWidth, height), content, style);
        }

        /// <summary>
        /// Computes the widget's screen-space rectangle (bottom-left origin, matching
        /// <see cref="Input.mousePosition"/>). Returns false when the rect is degenerate.
        /// </summary>
        internal static bool TryGetWidgetScreenRect(RectTransform rt, out Rect screenRect)
        {
            screenRect = default(Rect);
            if (rt == null)
                return false;

            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera
                : null;

            rt.GetWorldCorners(CornerBuf);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, CornerBuf[i]);
                if (sp.x < minX) minX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y > maxY) maxY = sp.y;
            }

            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return screenRect.width > 1f && screenRect.height > 1f;
        }

        // ================================================================
        // Tooltip text (pure / testable)
        // ================================================================

        /// <summary>
        /// Builds the "Total / Reserved" tooltip body. Always renders (even at Reserved 0) so
        /// the breakdown is discoverable - a hidden tooltip would leave the player wondering
        /// how much, if anything, is reserved. No header line: the hovered widget makes the
        /// currency obvious. <paramref name="available"/> is the value shown on the bar, so by
        /// construction the bar equals <paramref name="available"/> = Total - Reserved.
        /// </summary>
        internal static string BuildReservationTooltip(double total, double available, string format)
        {
            double reserved = total - available;
            if (reserved < 0.0)
                reserved = 0.0;

            return "Total: " + total.ToString(format, IC) + "\n"
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
            return BuildReservationTooltip(displayed + reserved, displayed, "N0");
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
            return BuildReservationTooltip(displayed + reserved, displayed, "F1");
        }
    }
}
