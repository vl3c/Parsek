using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    public partial class FlightRecorder
    {
        internal static PartEventType ClassifyPartDeath(
            uint partPersistentId, bool hasParachuteModule, Dictionary<uint, int> parachuteStates)
        {
            int state;
            if (hasParachuteModule && parachuteStates.TryGetValue(partPersistentId, out state) && state > 0)
            {
                parachuteStates.Remove(partPersistentId);
                return PartEventType.ParachuteDestroyed;
            }
            return PartEventType.Destroyed;
        }

        internal static void ClassifyLadderState(float animTime, out bool isExtended, out bool isRetracted)
        {
            isExtended = animTime >= 0.99f;
            isRetracted = animTime <= 0.01f;
        }

        internal static bool TryClassifyLadderStateFromEventActivity(
            bool canExtend, bool canRetract, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;

            // For ladders, mutually-exclusive UI event activity indicates current state:
            // - can retract => currently deployed
            // - can extend  => currently retracted
            if (canExtend == canRetract)
                return false;

            isDeployed = canRetract;
            isRetracted = canExtend;
            return true;
        }

        internal static bool TryClassifyRetractableLadderStateName(
            string stateName, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (string.IsNullOrWhiteSpace(stateName))
                return false;

            string normalized = stateName.Trim();
            // Stock uses Extended/Retracted; Deployed/Stowed covers modded ladder variants.
            if (string.Equals(normalized, "Extended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Deployed", StringComparison.OrdinalIgnoreCase))
            {
                isDeployed = true;
                return true;
            }

            if (string.Equals(normalized, "Retracted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Stowed", StringComparison.OrdinalIgnoreCase))
            {
                isRetracted = true;
                return true;
            }

            return false;
        }

        internal static bool IsRetractableLadderTransientStateName(string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName))
                return false;

            string normalized = stateName.Trim();
            return string.Equals(normalized, "Extending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Retracting", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Pure keyword classifier for an aero-surface event: marks an event as deploy when its
        /// lowercased name or gui name contains deploy/extend/open/brake/enable, and as retract
        /// when it contains retract/close/stow/disable. Both outputs can be set independently
        /// (an event matching neither set leaves both false). Inputs are expected lowercased.
        /// </summary>
        internal static void ClassifyAeroEventName(
            string evtName, string guiName, out bool isDeploy, out bool isRetract)
        {
            isDeploy =
                evtName.Contains("deploy") || guiName.Contains("deploy") ||
                evtName.Contains("extend") || guiName.Contains("extend") ||
                evtName.Contains("open") || guiName.Contains("open") ||
                evtName.Contains("brake") || guiName.Contains("brake") ||
                evtName.Contains("enable") || guiName.Contains("enable");
            isRetract =
                evtName.Contains("retract") || guiName.Contains("retract") ||
                evtName.Contains("close") || guiName.Contains("close") ||
                evtName.Contains("stow") || guiName.Contains("stow") ||
                evtName.Contains("disable") || guiName.Contains("disable");
        }

        internal static void ClassifyGearState(string stateString, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = stateString == "Deployed";
            isRetracted = stateString == "Retracted";
        }

        internal static void ClassifyCargoBayState(
            float animTime, float closedPosition, out bool isOpen, out bool isClosed)
        {
            bool atStart = animTime <= 0.01f;
            bool atEnd = animTime >= 0.99f;

            if (closedPosition > 0.9f)
            {
                // closedPosition near 1 → closed at animTime≈1, open at animTime≈0
                isClosed = atEnd;
                isOpen = atStart;
            }
            else if (closedPosition < 0.1f)
            {
                // closedPosition near 0 → closed at animTime≈0, open at animTime≈1
                isClosed = atStart;
                isOpen = atEnd;
            }
            else
            {
                // Non-standard closedPosition (modded part) — skip
                isClosed = false;
                isOpen = false;
            }
        }
    }
}
