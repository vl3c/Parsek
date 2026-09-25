using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// KSC facility context menu annotation on stock mechanisms (docs/dev/research/
    /// stock-ui-reservation-overlays-2026-09-25.md, section 5, the KSC facility row): a
    /// facility a committed row upgrades later gets a non-interactable Upgrade button
    /// (block at the control) carrying a stock <see cref="TooltipController_Text"/> with
    /// the explanation (why). The mark and the block read the predicate the
    /// <c>FacilityUpgradeSpendPatch</c> / <c>FacilityUpgradePatch</c> refusal reads, through
    /// <see cref="StockUiDecorationQuery.ForFacilityMenu"/>, with the same text.
    ///
    /// Stock instantiates a fresh menu per right-click (<c>KSCFacilityContextMenu.Create</c>)
    /// and destroys it on dismiss, so a menu's state never reaches another facility's menu.
    /// Inside one menu, stock's <c>OnFacilityValuesModified</c> re-sets the Upgrade button's
    /// interactable state on every run (open, structure collapse / repair, and the timeline
    /// refresh this class drives), so the block is re-applied after it and "lifting" only
    /// needs the reason removed.
    /// </summary>
    internal static class StockUiFacilityDecoration
    {
        private const string Tag = "StockUiOverlay";

        internal const string UpgradeButtonFieldName = "UpgradeButton";
        internal const string HostFieldName = "host";
        /// <summary>The fallback reason field: the building description, which stock writes
        /// once in <c>CreateWindowContent</c>. The level text (<c>levelStatsText</c>) is not
        /// used because hovering Upgrade rewrites it (<c>PreviewNextLevel</c>).</summary>
        internal const string DescriptionFieldName = "descriptionText";

        /// <summary>What the menu does with its Upgrade button for one decoration.</summary>
        internal struct UpgradeButtonDecision
        {
            internal bool DisableUpgrade;
            /// <summary>The explanation shown on the disabled button, or null.</summary>
            internal string Reason;
        }

        /// <summary>
        /// The facility menu decision: Upgrade is disabled, with the explanation, exactly
        /// when the decoration is blocked (the refusal set). An unblocked decoration leaves
        /// the button to stock.
        /// </summary>
        internal static UpgradeButtonDecision Decide(StockUiDecoration d)
        {
            if (!d.Blocked) return default(UpgradeButtonDecision);
            return new UpgradeButtonDecision { DisableUpgrade = true, Reason = d.Why };
        }

        /// <summary>
        /// The tooltip text on the Upgrade button: the explanation alone on a controller
        /// Parsek added, else the stock text with the explanation appended on its own line.
        /// </summary>
        internal static string ComposeTooltipText(bool parsekOwned, string stockText, string why)
        {
            return parsekOwned ? (why ?? "") : StockUiRnDDecoration.AppendReason(stockText, why);
        }

        /// <summary>Removes a line <see cref="StockUiRnDDecoration.AppendReason"/> added.
        /// Text without it is returned unchanged.</summary>
        internal static string RemoveReason(string text, string why)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(why)) return text;
            string line = "<color=" + StockUiRnDDecoration.ReasonColorHex + ">" + why + "</color>";
            string withBreak = "\n" + line;
            if (text.Contains(withBreak)) return text.Replace(withBreak, "");
            return text == line ? "" : text.Replace(line, "");
        }

        // ---------------- stock member access ----------------

        private static AccessTools.FieldRef<KSCFacilityContextMenu, Button> upgradeButtonRef;
        private static AccessTools.FieldRef<KSCFacilityContextMenu, SpaceCenterBuilding> hostRef;
        private static bool fieldRefsResolved;
        private static bool fieldRefsFailed;

        /// <summary>The private <c>UpgradeButton</c> accessor, or null when stock renamed it.</summary>
        internal static AccessTools.FieldRef<KSCFacilityContextMenu, Button> ResolveUpgradeButtonRefForTesting()
        {
            return AccessTools.FieldRefAccess<KSCFacilityContextMenu, Button>(UpgradeButtonFieldName);
        }

        /// <summary>The protected <c>host</c> accessor.</summary>
        internal static AccessTools.FieldRef<KSCFacilityContextMenu, SpaceCenterBuilding> ResolveHostRefForTesting()
        {
            return AccessTools.FieldRefAccess<KSCFacilityContextMenu, SpaceCenterBuilding>(HostFieldName);
        }

        private static bool TryResolveFieldRefs()
        {
            if (fieldRefsResolved) return true;
            if (fieldRefsFailed) return false;
            try
            {
                upgradeButtonRef = ResolveUpgradeButtonRefForTesting();
                hostRef = ResolveHostRefForTesting();
                fieldRefsResolved = upgradeButtonRef != null && hostRef != null;
            }
            catch (Exception ex)
            {
                fieldRefsResolved = false;
                ParsekLog.Warn(Tag, "KSCFacilityContextMenu." + UpgradeButtonFieldName + " / ." + HostFieldName
                    + " not accessible (" + ex.GetType().Name + ": " + ex.Message + ") - the facility menu Upgrade "
                    + "button will not be disabled (FacilityUpgradeSpendPatch still refuses the click)");
            }
            if (!fieldRefsResolved) fieldRefsFailed = true;
            return fieldRefsResolved;
        }

        /// <summary>The menu's upgradeable facility id (<c>SpaceCenter/LaunchPad</c>), or null.</summary>
        internal static string FacilityIdOf(KSCFacilityContextMenu menu)
        {
            if (menu == null || !TryResolveFieldRefs()) return null;
            SpaceCenterBuilding host = hostRef(menu);
            if (host == null) return null;
            var facility = host.Facility;
            if (facility == null) return null;
            return facility.id;
        }

        /// <summary>The menu's stock Upgrade button, or null.</summary>
        internal static Button UpgradeButtonOf(KSCFacilityContextMenu menu)
        {
            if (menu == null || !TryResolveFieldRefs()) return null;
            return upgradeButtonRef(menu);
        }

        // ---------------- per-menu state ----------------

        /// <summary>What Parsek changed on one open menu, so a lifted block restores it.</summary>
        internal sealed class MenuState
        {
            internal TooltipController_Text Tooltip;
            internal bool TooltipOwned;
            internal string StockTooltipText;
            internal bool StockRequireInteractable;
            internal bool StockTooltipEnabled;
            /// <summary>The explanation appended to the description (the fallback), or null.</summary>
            internal string DescriptionReason;
        }

        private static ConditionalWeakTable<KSCFacilityContextMenu, MenuState> menuStates =
            new ConditionalWeakTable<KSCFacilityContextMenu, MenuState>();

        /// <summary>The Parsek state of an open menu, or null when Parsek never decorated it.</summary>
        internal static MenuState StateOf(KSCFacilityContextMenu menu)
        {
            if (menu == null) return null;
            MenuState state;
            return menuStates.TryGetValue(menu, out state) ? state : null;
        }

        // ---------------- tooltip prefab ----------------

        private static Tooltip_Text cachedTooltipPrefab;
        private static bool tooltipPrefabMissingLogged;

        /// <summary>
        /// A stock <see cref="Tooltip_Text"/> prefab to give an added controller (a
        /// <see cref="TooltipController_Text"/> added from code has none and draws nothing):
        /// copied from a stock controller in the menu, else in the scene, else among the
        /// loaded UI assets. Cached once found; logged once when none exists.
        /// </summary>
        internal static Tooltip_Text FindTooltipPrefab(Component scope)
        {
            if (cachedTooltipPrefab != null) return cachedTooltipPrefab;

            string source = null;
            Tooltip_Text found = null;
            if (scope != null)
            {
                found = FirstPrefab(scope.GetComponentsInChildren<TooltipController_Text>(true));
                if (found != null) source = "the facility menu";
            }
            if (found == null)
            {
                found = FirstPrefab(UnityEngine.Object.FindObjectsOfType<TooltipController_Text>());
                if (found != null) source = "the scene";
            }
            if (found == null)
            {
                found = FirstPrefab(Resources.FindObjectsOfTypeAll<TooltipController_Text>());
                if (found != null) source = "the loaded UI assets";
            }

            if (found != null)
            {
                cachedTooltipPrefab = found;
                ParsekLog.Info(Tag, "FacilityMenu tooltip prefab copied from a stock TooltipController_Text in "
                    + source + " (prefab '" + found.name + "')");
                return found;
            }

            if (!tooltipPrefabMissingLogged)
            {
                tooltipPrefabMissingLogged = true;
                ParsekLog.Warn(Tag, "FacilityMenu: no stock TooltipController_Text with a prefab found - the "
                    + "Upgrade explanation falls back to the facility menu's description text");
            }
            return null;
        }

        private static Tooltip_Text FirstPrefab(TooltipController_Text[] controllers)
        {
            if (controllers == null) return null;
            for (int i = 0; i < controllers.Length; i++)
            {
                var c = controllers[i];
                if (c != null && c.prefab != null) return c.prefab;
            }
            return null;
        }

        // ---------------- live applicator (Harmony postfix body) ----------------

        /// <summary>
        /// <c>KSCFacilityContextMenu.OnFacilityValuesModified</c> postfix: stock has just set
        /// the Upgrade button from the facility level; disable it with the reason when the
        /// committed timeline upgrades this facility later, else clear any reason Parsek
        /// left on this menu.
        /// </summary>
        internal static void Apply(KSCFacilityContextMenu menu, string reason)
        {
            if (menu == null || !TryResolveFieldRefs()) return;
            string facilityId = FacilityIdOf(menu);
            bool replaying = GameStateRecorder.IsReplayingActions;
            var snapshot = StockUiLiveSnapshot.Current;
            var d = StockUiDecorationQuery.ForFacilityMenu(snapshot.Index, snapshot.UT, facilityId, replaying,
                ReservationExplanation.DefaultDateFormatter);
            StockUiDecorationQuery.LogFacilityMenu(d, replaying, reason);

            var decision = Decide(d);
            MenuState state = StateOf(menu);
            if (decision.DisableUpgrade)
            {
                Button upgrade = upgradeButtonRef(menu);
                if (upgrade == null)
                {
                    ParsekLog.Verbose(Tag, "FacilityMenu " + facilityId + ": no Upgrade button on the menu - nothing to disable");
                    return;
                }
                if (state == null) state = menuStates.GetValue(menu, _ => new MenuState());
                upgrade.interactable = false;
                string where = ShowReason(menu, upgrade, state, decision.Reason);
                ParsekLog.Verbose(Tag, "FacilityMenu " + facilityId + ": Upgrade disabled, reason on the " + where);
                return;
            }

            if (state != null && ClearReason(menu, state))
                ParsekLog.Verbose(Tag, "FacilityMenu " + (facilityId ?? "<none>")
                    + ": block lifted - Upgrade left to stock, Parsek reason removed");
        }

        private static string ShowReason(KSCFacilityContextMenu menu, Button upgrade, MenuState state, string why)
        {
            if (state.Tooltip == null)
            {
                var existing = upgrade.GetComponent<TooltipController_Text>();
                if (existing != null)
                {
                    state.Tooltip = existing;
                    state.TooltipOwned = false;
                    state.StockTooltipText = existing.textString;
                    state.StockRequireInteractable = existing.RequireInteractable;
                    state.StockTooltipEnabled = existing.enabled;
                }
                else
                {
                    var prefab = FindTooltipPrefab(menu);
                    if (prefab != null)
                    {
                        var added = upgrade.gameObject.AddComponent<TooltipController_Text>();
                        added.prefab = prefab;
                        state.Tooltip = added;
                        state.TooltipOwned = true;
                    }
                }
            }

            var tip = state.Tooltip;
            if (tip != null && tip.prefab == null)
            {
                var prefab = FindTooltipPrefab(menu);
                if (prefab != null) tip.prefab = prefab;
            }

            if (tip != null && tip.prefab != null)
            {
                tip.RequireInteractable = false;
                tip.SetText(ComposeTooltipText(state.TooltipOwned, state.StockTooltipText, why));
                tip.enabled = true;
                RemoveDescriptionReason(menu, state);
                return "stock tooltip";
            }

            AppendDescriptionReason(menu, state, why);
            return "description text (no stock tooltip prefab)";
        }

        private static bool ClearReason(KSCFacilityContextMenu menu, MenuState state)
        {
            bool changed = false;
            var tip = state.Tooltip;
            if (tip != null)
            {
                if (state.TooltipOwned)
                {
                    if (tip.enabled || !string.IsNullOrEmpty(tip.textString))
                    {
                        tip.SetText("");
                        tip.enabled = false;
                        changed = true;
                    }
                }
                else if (tip.textString != state.StockTooltipText
                         || tip.RequireInteractable != state.StockRequireInteractable
                         || tip.enabled != state.StockTooltipEnabled)
                {
                    tip.SetText(state.StockTooltipText);
                    tip.RequireInteractable = state.StockRequireInteractable;
                    tip.enabled = state.StockTooltipEnabled;
                    changed = true;
                }
            }
            if (RemoveDescriptionReason(menu, state)) changed = true;
            return changed;
        }

        private static void AppendDescriptionReason(KSCFacilityContextMenu menu, MenuState state, string why)
        {
            object label = StockUiText.LabelField(menu, typeof(KSCFacilityContextMenu), DescriptionFieldName);
            string text = StockUiText.Get(label);
            if (text == null) return;
            if (state.DescriptionReason != null && state.DescriptionReason != why)
                text = RemoveReason(text, state.DescriptionReason);
            StockUiText.Set(label, StockUiRnDDecoration.AppendReason(text, why));
            state.DescriptionReason = why;
        }

        private static bool RemoveDescriptionReason(KSCFacilityContextMenu menu, MenuState state)
        {
            if (state.DescriptionReason == null) return false;
            object label = StockUiText.LabelField(menu, typeof(KSCFacilityContextMenu), DescriptionFieldName);
            string text = StockUiText.Get(label);
            if (text != null)
                StockUiText.Set(label, RemoveReason(text, state.DescriptionReason));
            state.DescriptionReason = null;
            return true;
        }

        // ---------------- open menus (timeline refresh) ----------------

        private static readonly List<KSCFacilityContextMenu> openMenus = new List<KSCFacilityContextMenu>();
        private static MethodInfo valuesModifiedMethod;

        /// <summary><c>GameEvents.onFacilityContextMenuSpawn</c>: track the menu so a timeline
        /// change can re-run its stock refresh. The buttons are not filled yet here, so
        /// nothing is decorated.</summary>
        internal static void OnMenuSpawned(KSCFacilityContextMenu menu)
        {
            PruneClosedMenus();
            if (menu != null && !openMenus.Contains(menu)) openMenus.Add(menu);
        }

        /// <summary><c>GameEvents.onFacilityContextMenuDespawn</c>.</summary>
        internal static void OnMenuDespawned(KSCFacilityContextMenu menu)
        {
            openMenus.Remove(menu);
            PruneClosedMenus();
        }

        /// <summary>The number of facility menus still open.</summary>
        internal static int OpenMenuCount
        {
            get
            {
                PruneClosedMenus();
                return openMenus.Count;
            }
        }

        private static void PruneClosedMenus()
        {
            // A menu terminated by a scene change is destroyed without a despawn event.
            openMenus.RemoveAll(m => m == null);
        }

        /// <summary>
        /// Re-runs stock's <c>OnFacilityValuesModified</c> on every open facility menu (its
        /// postfix re-decorates), so a committed-timeline change while a menu is open greys
        /// or restores Upgrade at once. A clock that passes the committed UT while the menu
        /// stays open is picked up on the next stock refresh or the next open; the click
        /// refusal reads the live clock either way.
        /// </summary>
        internal static void RefreshOpenMenus(string reason)
        {
            PruneClosedMenus();
            if (openMenus.Count == 0) return;
            if (valuesModifiedMethod == null)
                valuesModifiedMethod = Patches.FacilityMenuUpgradeBlockPatch.ResolveTargetMethodForTesting() as MethodInfo;
            if (valuesModifiedMethod == null) return;
            var menus = openMenus.ToArray();
            for (int i = 0; i < menus.Length; i++)
            {
                try
                {
                    valuesModifiedMethod.Invoke(menus[i], null);
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    ParsekLog.WarnRateLimited(Tag, "facility-menu-refresh-failed",
                        "FacilityMenu refresh (" + (reason ?? "refresh") + ") failed (" + inner.GetType().Name + ": "
                        + inner.Message + ")");
                }
            }
            ParsekLog.Verbose(Tag, "FacilityMenu: re-ran the stock refresh on " + menus.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " open menu(s) (" + (reason ?? "refresh") + ")");
        }

        internal static void ResetForTesting()
        {
            menuStates = new ConditionalWeakTable<KSCFacilityContextMenu, MenuState>();
            openMenus.Clear();
            cachedTooltipPrefab = null;
            tooltipPrefabMissingLogged = false;
            fieldRefsResolved = false;
            fieldRefsFailed = false;
            upgradeButtonRef = null;
            hostRef = null;
            valuesModifiedMethod = null;
        }
    }
}
