using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The inert game-mode passthrough and the pre-Parsek backup hold: keep the loaded node and
    // write it back unchanged.
    public partial class ParsekScenario
    {
        private bool ShouldKeepBackupHold()
        {
            string saveFolder = HighLogic.SaveFolder;
            if (!PreParsekBackup.IsHoldActiveFor(saveFolder))
                return false;
            // The held instance needs the main-menu hook too, or a main-menu round trip
            // would never reset initialLoadDone and the hold could never clear.
            RegisterMainMenuHook();
            string saveFolderBeforeDetect = lastSaveFolder;
            DetectSaveFolderChange();
            if (!initialLoadDone)
            {
                PreParsekBackup.ClearHold("fresh load from the main menu: retrying the backup");
                // The cold path's own detect now sees no change; hand it the folder this one
                // saw, so the retry load classifies with its real cold reason.
                backupHoldRetrySaveFolderBefore = saveFolderBeforeDetect;
                backupHoldRetryDetected = true;
                return false;
            }
            return true;
        }

        /// <summary>Arms the hold for <paramref name="saveFolder"/> from the failed cold load.</summary>
        private void EnterBackupHold(ConfigNode node, string saveFolder)
        {
            PreParsekBackup.ArmHold(saveFolder, "first-contact-backup-failed");
            // Later loads of this save in this session are not fresh: they stay held.
            initialLoadDone = true;
            StashInertGameModeNode(node, BackupHoldInertReason);
            // Posted once the scene settles (an OnLoad message can land under the loading screen).
            ParsekHarmony.QueueNoticeForSettledScene(PreParsekBackup.HoldScreenMessage);
        }

        /// <summary>Inert-game-mode OnLoad body (kept out of OnLoad so it is testable headless).</summary>
        internal void StashInertGameModeNode(ConfigNode node, string inertReason = "inert game mode")
        {
            inertGameModePassthroughNode = node != null ? node.CreateCopy() : null;
            inertPassthroughReason = inertReason;
            ParsekLog.Info("Scenario",
                "OnLoad: " + inertReason + " - Parsek node kept verbatim for save pass-through ("
                + (inertGameModePassthroughNode != null
                    ? inertGameModePassthroughNode.values.Count + " value(s), "
                      + inertGameModePassthroughNode.nodes.Count + " node(s)"
                    : "no node")
                + "); nothing loaded");
        }

        /// <summary>Inert-game-mode OnSave body.</summary>
        internal void WriteInertGameModeNode(ConfigNode node)
        {
            int copied = CopyInertPassthroughNode(inertGameModePassthroughNode, node);
            ParsekLog.Info("Scenario",
                "OnSave: " + inertPassthroughReason + " - wrote the loaded Parsek node back verbatim ("
                + copied.ToString(CultureInfo.InvariantCulture)
                + (copied == 1 ? " entry" : " entries") + "); no Parsek state saved");
        }

        /// <summary>
        /// Copies <paramref name="loaded"/> into
        /// <paramref name="target"/> verbatim, minus the stock <c>name</c> / <c>scene</c>
        /// values <c>ScenarioModule.Save</c> already wrote. Returns the number of values plus
        /// child nodes copied; 0 when nothing was loaded (then nothing is written, and there
        /// was nothing to erase).
        /// </summary>
        internal static int CopyInertPassthroughNode(ConfigNode loaded, ConfigNode target)
        {
            if (loaded == null || target == null)
                return 0;
            int copied = 0;
            for (int i = 0; i < loaded.values.Count; i++)
            {
                var v = loaded.values[i];
                if (v == null || v.name == "name" || v.name == "scene")
                    continue;
                target.AddValue(v.name, v.value);
                copied++;
            }
            for (int i = 0; i < loaded.nodes.Count; i++)
            {
                var child = loaded.nodes[i];
                if (child == null)
                    continue;
                target.AddNode(child.CreateCopy());
                copied++;
            }
            return copied;
        }
    }
}
