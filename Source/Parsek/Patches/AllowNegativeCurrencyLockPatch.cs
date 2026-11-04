using System.Reflection;
using HarmonyLib;
using KSP.Localization;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Greys the stock "Allow negative funds / science" switch in the mid-career difficulty
    /// dialog once the career has a lock (owner ruling 2026-11-04, <see cref="CareerSettingsLock"/>),
    /// and appends the reason to that switch's own stock tooltip (stock-control annotation,
    /// owner ruling D1: the control's disabled state plus text appended to its existing
    /// tooltip). The new-game dialog is left alone: the value picked there is the one the
    /// career locks. Stock builds each switch's interactable condition from
    /// <c>CustomParameterNode.Interactible</c> and its tooltip from
    /// <c>Localizer.Format(CustomParameterUI.toolTip)</c> (DifficultyOptionsMenu, decompiled),
    /// so the mark lives from <c>DifficultyOptionsMenu.Create</c> until the dialog's dismiss.
    /// </summary>
    internal static class AllowNegativeCurrencyLockPatch
    {
        internal const string Tag = "CareerSettings";
        internal const string MemberName = "AllowNegativeCurrency";

        /// <summary>The stock tooltip tag of the switch (<c>AdvancedParams.AllowNegativeCurrency</c>'s
        /// <c>CustomParameterUI.toolTip</c>, decompiled).</summary>
        internal const string TooltipTag = "#autoLoc_6002203";

        internal const string LockedTooltipSuffix =
            " Parsek: locked for this career, because changing it would rewrite past penalties.";

        private static bool dialogLocked;
        private static string originalTooltip;

        internal static bool DialogLocked => dialogLocked;

        /// <summary>Pure: the switch is greyed only in a mid-career dialog of a career that
        /// has a lock.</summary>
        internal static bool ShouldLock(bool newGame, bool? careerLock)
        {
            return !newGame && careerLock.HasValue;
        }

        /// <summary>Pure: whether stock's interactable answer for <paramref name="memberName"/>
        /// is overridden to false.</summary>
        internal static bool BlocksMember(bool locked, string memberName)
        {
            return locked && memberName == MemberName;
        }

        internal static string LockedTooltip(string original)
        {
            return (original ?? string.Empty) + LockedTooltipSuffix;
        }

        internal static void BeginDialog(bool newGame)
        {
            EndDialog("new dialog");
            if (!ShouldLock(newGame, CareerSettingsLock.AllowNegative))
                return;
            dialogLocked = true;
            string current;
            if (Localizer.Instance != null && Localizer.TryGetStringByTag(TooltipTag, out current))
            {
                originalTooltip = current;
                Localizer.Tags[TooltipTag] = LockedTooltip(current);
            }
            bool value = CareerSettingsLock.AllowNegative.Value;
            ParsekLog.Info(Tag,
                $"Difficulty dialog opened mid-career: allow negative currency switch greyed " +
                $"(locked {(value ? "on" : "off")}; tooltip {(originalTooltip != null ? "annotated" : "not found")})");
        }

        internal static void EndDialog(string reason)
        {
            if (originalTooltip != null)
            {
                if (Localizer.Instance != null)
                    Localizer.Tags[TooltipTag] = originalTooltip;
                originalTooltip = null;
            }
            if (!dialogLocked)
                return;
            dialogLocked = false;
            ParsekLog.Verbose(Tag, $"Allow negative currency switch mark removed ({reason})");
        }
    }

    [HarmonyPatch]
    internal static class AllowNegativeCurrencyLockDialogPatch
    {
        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(DifficultyOptionsMenu),
                nameof(DifficultyOptionsMenu.Create),
                new[]
                {
                    typeof(Game.Modes), typeof(GameParameters), typeof(bool),
                    typeof(Callback<GameParameters, bool>), typeof(GameObject),
                });
            if (method == null)
                ParsekLog.Warn(AllowNegativeCurrencyLockPatch.Tag,
                    "DifficultyOptionsMenu.Create(5 args) not found - the allow negative currency switch " +
                    "stays clickable; the career lock still holds the value. Harmony will skip this patch.");
            return method;
        }

        static void Prefix(bool newGame)
        {
            if (ParsekGameModeGate.CheckInert("AllowNegativeCurrencyLockDialogPatch.Prefix")) return;
            AllowNegativeCurrencyLockPatch.BeginDialog(newGame);
        }
    }

    [HarmonyPatch]
    internal static class AllowNegativeCurrencyLockInteractiblePatch
    {
        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(GameParameters.AdvancedParams),
                nameof(GameParameters.AdvancedParams.Interactible),
                new[] { typeof(MemberInfo), typeof(GameParameters) });
            if (method == null)
                ParsekLog.Warn(AllowNegativeCurrencyLockPatch.Tag,
                    "AdvancedParams.Interactible not found - the allow negative currency switch stays " +
                    "clickable; the career lock still holds the value. Harmony will skip this patch.");
            return method;
        }

        static void Postfix(MemberInfo member, ref bool __result)
        {
            if (ParsekGameModeGate.CheckInert("AllowNegativeCurrencyLockInteractiblePatch.Postfix")) return;
            if (AllowNegativeCurrencyLockPatch.BlocksMember(
                    AllowNegativeCurrencyLockPatch.DialogLocked, member?.Name))
                __result = false;
        }
    }
}
