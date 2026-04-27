using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    internal enum TrackingStationGhostActionKind
    {
        Focus,
        SetTarget,
        ShowRecording,
        Materialize,
        Fly,
        Delete,
        Recover
    }

    internal enum TrackingStationGhostActionSafety
    {
        SafeOnGhost,
        SafeWhenEligible,
        SafeOnlyAfterMaterialization,
        BlockedOnGhost
    }

    internal readonly struct TrackingStationGhostActionState
    {
        internal TrackingStationGhostActionState(
            TrackingStationGhostActionKind kind,
            string label,
            TrackingStationGhostActionSafety safety,
            bool enabled,
            string reason)
        {
            Kind = kind;
            Label = label ?? string.Empty;
            Safety = safety;
            Enabled = enabled;
            Reason = reason ?? string.Empty;
        }

        internal TrackingStationGhostActionKind Kind { get; }
        internal string Label { get; }
        internal TrackingStationGhostActionSafety Safety { get; }
        internal bool Enabled { get; }
        internal string Reason { get; }
    }

    internal readonly struct TrackingStationGhostActionContext
    {
        internal TrackingStationGhostActionContext(
            bool hasGhostVessel,
            bool canFocus,
            bool canSetTarget,
            int recordingIndex,
            bool hasRecording,
            bool materializeEligible,
            string materializeReason,
            bool alreadyMaterialized)
        {
            HasGhostVessel = hasGhostVessel;
            CanFocus = canFocus;
            CanSetTarget = canSetTarget;
            RecordingIndex = recordingIndex;
            HasRecording = hasRecording;
            MaterializeEligible = materializeEligible;
            MaterializeReason = materializeReason;
            AlreadyMaterialized = alreadyMaterialized;
        }

        internal bool HasGhostVessel { get; }
        internal bool CanFocus { get; }
        internal bool CanSetTarget { get; }
        internal int RecordingIndex { get; }
        internal bool HasRecording { get; }
        internal bool MaterializeEligible { get; }
        internal string MaterializeReason { get; }
        internal bool AlreadyMaterialized { get; }
    }

    internal static class TrackingStationGhostActionPresentation
    {
        // Stock Fly / Delete / Recover are intentionally absent from the
        // action panel: they are blocked on ghost ProtoVessels by the
        // GhostTracking{Fly,Delete,Recover} patches and only ever rendered as
        // permanently-disabled buttons here. Removing them tightens the panel
        // to the four real actions and lets the Materialize button claim its
        // own row at full width.
        internal static TrackingStationGhostActionState[] BuildActionStates(
            TrackingStationGhostActionContext context)
        {
            bool hasRecording = context.RecordingIndex >= 0 && context.HasRecording;
            bool canMaterialize = hasRecording
                && context.MaterializeEligible
                && !context.AlreadyMaterialized;

            return new[]
            {
                new TrackingStationGhostActionState(
                    TrackingStationGhostActionKind.Focus,
                    "Focus",
                    TrackingStationGhostActionSafety.SafeOnGhost,
                    context.HasGhostVessel && context.CanFocus,
                    context.HasGhostVessel && context.CanFocus
                        ? "Center the Tracking Station camera on this ghost."
                        : "Tracking Station camera or ghost map object is not ready."),

                new TrackingStationGhostActionState(
                    TrackingStationGhostActionKind.SetTarget,
                    "Target",
                    TrackingStationGhostActionSafety.SafeOnGhost,
                    context.HasGhostVessel && context.CanSetTarget,
                    context.HasGhostVessel && context.CanSetTarget
                        ? "Set this ghost as the navigation target."
                        : "Targeting is not available in the current Tracking Station state."),

                new TrackingStationGhostActionState(
                    TrackingStationGhostActionKind.ShowRecording,
                    "Recording",
                    TrackingStationGhostActionSafety.SafeOnGhost,
                    hasRecording,
                    hasRecording
                        ? "Open the owning recording details."
                        : "This chain ghost has no direct committed recording row."),

                new TrackingStationGhostActionState(
                    TrackingStationGhostActionKind.Materialize,
                    "Materialize",
                    TrackingStationGhostActionSafety.SafeWhenEligible,
                    canMaterialize,
                    DescribeMaterializeReason(context, hasRecording))
            };
        }

        private static string DescribeMaterializeReason(
            TrackingStationGhostActionContext context,
            bool hasRecording)
        {
            if (!hasRecording)
                return "No committed recording is attached to this ghost.";
            if (context.AlreadyMaterialized)
                return "The owning recording is already materialized.";
            if (context.MaterializeEligible)
                return "Spawn the recorded vessel at its resolved Tracking Station endpoint.";

            switch (context.MaterializeReason)
            {
                case GhostMapPresence.TrackingStationSpawnSkipBeforeEnd:
                    return "Recording has not reached its endpoint yet.";
                case GhostMapPresence.TrackingStationSpawnSkipRewindPending:
                    return "Waiting for the pending rewind UT adjustment.";
                case GhostMapPresence.TrackingStationSpawnSkipIntermediateChainSegment:
                case GhostMapPresence.TrackingStationSpawnSkipIntermediateGhostChainLink:
                    return "Intermediate chain segments materialize through their chain tip.";
                case GhostMapPresence.TrackingStationSpawnSkipTerminatedGhostChain:
                    return "Terminated ghost chains do not materialize.";
                case null:
                case "":
                    return "Recording is not eligible to materialize yet.";
                default:
                    return "Materialize blocked: " + context.MaterializeReason;
            }
        }
    }

    internal readonly struct TrackingStationGhostSelectionInfo
    {
        internal TrackingStationGhostSelectionInfo(
            uint ghostPid,
            string vesselName,
            int recordingIndex,
            string recordingId,
            double startUT,
            double endUT,
            TerminalState? terminalState,
            bool vesselSpawned,
            uint spawnedVesselPersistentId,
            bool hasRecording)
        {
            GhostPid = ghostPid;
            VesselName = vesselName ?? "Ghost";
            RecordingIndex = recordingIndex;
            RecordingId = recordingId;
            StartUT = startUT;
            EndUT = endUT;
            TerminalState = terminalState;
            VesselSpawned = vesselSpawned;
            SpawnedVesselPersistentId = spawnedVesselPersistentId;
            HasRecording = hasRecording;
        }

        internal uint GhostPid { get; }
        internal string VesselName { get; }
        internal int RecordingIndex { get; }
        internal string RecordingId { get; }
        internal double StartUT { get; }
        internal double EndUT { get; }
        internal TerminalState? TerminalState { get; }
        internal bool VesselSpawned { get; }
        internal uint SpawnedVesselPersistentId { get; }
        internal bool HasRecording { get; }
    }

    /// <summary>
    /// Prevents tracking station actions (Fly, Delete, Recover) on ghost map ProtoVessels.
    /// Ghost vessels are transient map-presence objects — they cannot be flown, deleted, or recovered.
    /// Shows a screen message explaining the ghost status instead.
    ///
    /// IMPORTANT: Delete and Recover patches must call OnDialogDismiss on the SpaceTracking
    /// instance to release the input lock set by the confirmation dialog. Returning false
    /// skips the original method which contains the unlock call.
    /// </summary>

    /// <summary>
    /// Ensures ghost vessels have orbit renderers before buildVesselsList iterates them,
    /// and suppresses any remaining NREs as a safety net.
    ///
    /// Root cause (#195): ghost ProtoVessels are created in the SpaceTracking.Awake prefix
    /// when MapView.fetch may not yet be initialized (Unity Awake ordering is undefined).
    /// Vessel.AddOrbitRenderer() silently bails when MapView.fetch is null, leaving
    /// orbitRenderer null. buildVesselsList (decompiled line 751) unconditionally accesses
    /// vessel.orbitRenderer.onVesselIconClicked.Add(...) with NO try/catch in the for loop,
    /// so a single NRE aborts the entire method including ConstructUIList().
    ///
    /// The Prefix calls EnsureGhostOrbitRenderers() which uses Traverse to invoke the
    /// private AddOrbitRenderer() on ghosts with null orbitRenderer. By the time
    /// buildVesselsList runs (from Start or event handlers), all Awakes have completed
    /// and MapView.fetch is guaranteed available.
    ///
    /// The Finalizer remains as a safety net for the known ghost ProtoVessel
    /// missing-orbitRenderer NRE only. Other stock failures are logged and left
    /// visible so they are not hidden behind Parsek's ghost guard.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "buildVesselsList")]
    internal static class GhostTrackingBuildVesselsListPatch
    {
        // KSP 1.12.5 Assembly-CSharp.dll: vessel.orbitRenderer.onVesselIconClicked.
        private const int BuildVesselsListOrbitRendererLoadOffset = 0x00b4;
        private const int BuildVesselsListIconEventLoadOffset = 0x00b9;

        static void Prefix()
        {
            GhostMapPresence.EnsureGhostOrbitRenderers();
        }

        static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            return FinalizeBuildVesselsListException(
                __exception,
                CreateBuildVesselsListExceptionContext(),
                __exception.StackTrace);
        }

        internal static Exception FinalizeBuildVesselsListExceptionForTesting(
            Exception exception,
            int totalVessels,
            int ghostVesselCount,
            int ghostMissingOrbitRendererCount,
            int nonGhostMissingOrbitRendererCount,
            bool firstMissingOrbitRendererIsGhost,
            int potentialEarlierStockNullCandidateCount = 0,
            string scanError = null,
            string exceptionStackTrace = null)
        {
            return FinalizeBuildVesselsListException(
                exception,
                new BuildVesselsListExceptionContext
                {
                    TotalVessels = totalVessels,
                    GhostVesselCount = ghostVesselCount,
                    GhostMissingOrbitRendererCount = ghostMissingOrbitRendererCount,
                    NonGhostMissingOrbitRendererCount = nonGhostMissingOrbitRendererCount,
                    FirstMissingOrbitRendererIsGhost = firstMissingOrbitRendererIsGhost,
                    PotentialEarlierStockNullCandidateCount = potentialEarlierStockNullCandidateCount,
                    ScanError = scanError
                },
                exceptionStackTrace);
        }

        private static Exception FinalizeBuildVesselsListException(
            Exception exception,
            BuildVesselsListExceptionContext context,
            string exceptionStackTrace)
        {
            if (exception == null)
                return null;

            if (IsKnownGhostProtoVesselNre(exception, context, exceptionStackTrace))
            {
                ParsekLog.VerboseRateLimited("GhostMap", "buildVesselsListGhostNRE",
                    string.Format(CultureInfo.InvariantCulture,
                        "Suppressed known ghost ProtoVessel SpaceTracking.buildVesselsList NRE: " +
                        "type={0} totalVessels={1} ghostVessels={2} ghostMissingOrbitRenderers={3} " +
                        "firstMissingOrbitRenderer=ghost",
                        exception.GetType().Name,
                        context.TotalVessels,
                        context.GhostVesselCount,
                        context.GhostMissingOrbitRendererCount));
                return null; // swallow — return null tells Harmony to suppress the exception
            }

            ParsekLog.Warn("GhostMap",
                string.Format(CultureInfo.InvariantCulture,
                    "SpaceTracking.buildVesselsList exception left visible: type={0} " +
                    "totalVessels={1} ghostVessels={2} ghostMissingOrbitRenderers={3} " +
                    "nonGhostMissingOrbitRenderers={4} firstMissingOrbitRenderer={5} " +
                    "priorStockNullCandidates={6}{7} message=\"{8}\"",
                    exception.GetType().Name,
                    context.TotalVessels,
                    context.GhostVesselCount,
                    context.GhostMissingOrbitRendererCount,
                    context.NonGhostMissingOrbitRendererCount,
                    context.FirstMissingOrbitRendererIsGhost ? "ghost" : "non-ghost-or-none",
                    context.PotentialEarlierStockNullCandidateCount,
                    FormatScanError(context.ScanError),
                    exception.Message ?? string.Empty));
            return exception;
        }

        private static bool IsKnownGhostProtoVesselNre(
            Exception exception,
            BuildVesselsListExceptionContext context,
            string exceptionStackTrace)
        {
            return exception is NullReferenceException
                && context.GhostMissingOrbitRendererCount > 0
                && context.FirstMissingOrbitRendererIsGhost
                && context.PotentialEarlierStockNullCandidateCount == 0
                && !StackTraceRulesOutKnownGhostRendererNre(exceptionStackTrace)
                && string.IsNullOrEmpty(context.ScanError);
        }

        private static BuildVesselsListExceptionContext CreateBuildVesselsListExceptionContext()
        {
            var context = new BuildVesselsListExceptionContext();
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null)
                    return context;

                context.TotalVessels = vessels.Count;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel vessel = vessels[i];
                    if (vessel == null)
                    {
                        if (!context.FoundFirstMissingOrbitRenderer)
                            context.PotentialEarlierStockNullCandidateCount++;
                        continue;
                    }

                    bool ghost = GhostMapPresence.IsGhostMapVessel(vessel.persistentId);
                    if (vessel.DiscoveryInfo == null && !context.FoundFirstMissingOrbitRenderer)
                        context.PotentialEarlierStockNullCandidateCount++;

                    bool missingOrbitRenderer = vessel.orbitRenderer == null;
                    if (missingOrbitRenderer && !context.FoundFirstMissingOrbitRenderer)
                    {
                        context.FoundFirstMissingOrbitRenderer = true;
                        context.FirstMissingOrbitRendererIsGhost = ghost;
                    }

                    if (ghost)
                    {
                        context.GhostVesselCount++;
                        if (missingOrbitRenderer)
                            context.GhostMissingOrbitRendererCount++;
                    }
                    else if (missingOrbitRenderer)
                    {
                        context.NonGhostMissingOrbitRendererCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                context.ScanError = $"{ex.GetType().Name}: {ex.Message}";
            }

            return context;
        }

        private static bool StackTraceRulesOutKnownGhostRendererNre(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return false;

            if (!TryFindBuildVesselsListIlOffset(stackTrace, out int offset))
                return false;

            return offset != BuildVesselsListOrbitRendererLoadOffset
                && offset != BuildVesselsListIconEventLoadOffset;
        }

        private static bool TryFindBuildVesselsListIlOffset(string stackTrace, out int offset)
        {
            offset = 0;
            string[] lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains("SpaceTracking.buildVesselsList"))
                    continue;

                int marker = line.IndexOf("[0x", StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                    return false;

                int start = marker + 3;
                int end = line.IndexOf(']', start);
                if (end <= start)
                    return false;

                string hex = line.Substring(start, end - start);
                return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
            }

            return false;
        }

        private static string FormatScanError(string scanError)
        {
            return string.IsNullOrEmpty(scanError)
                ? string.Empty
                : $" scanError=\"{scanError}\"";
        }

        private struct BuildVesselsListExceptionContext
        {
            public int TotalVessels;
            public int GhostVesselCount;
            public int GhostMissingOrbitRendererCount;
            public int NonGhostMissingOrbitRendererCount;
            public bool FoundFirstMissingOrbitRenderer;
            public bool FirstMissingOrbitRendererIsGhost;
            public int PotentialEarlierStockNullCandidateCount;
            public string ScanError;
        }
    }

    /// <summary>
    /// Creates ghost map ProtoVessels before SpaceTracking.Start loads the game state.
    /// SpaceTracking.Awake only registers events — vessel iteration happens in
    /// buildVesselsList() called from Start(). Creating ghosts early ensures they are
    /// in FlightGlobals.Vessels before st.Load() and buildVesselsList() run.
    /// Note: orbitRenderer may be null after creation here if MapView.Awake hasn't
    /// run yet — this is fixed by the buildVesselsList Prefix calling
    /// EnsureGhostOrbitRenderers().
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "Awake")]
    internal static class GhostTrackingStationInitPatch
    {
        static void Prefix()
        {
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            if (created > 0)
                ParsekLog.Info("GhostMap",
                    $"Pre-created {created} ghost vessel(s) before SpaceTracking.Awake — " +
                    "ensures map icon click handlers are registered");
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "FlyVessel")]
    internal static class GhostTrackingFlyPatch
    {
        static bool Prefix(SpaceTracking __instance, Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            GhostTrackingStationSelection.SelectGhost(v, "Fly block");
            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost vessel — it will materialize when its timeline reaches the spawn point.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked FlyVessel for ghost '{v.vesselName}' pid={v.persistentId}");

            GhostTrackingStationSelection.TryClearSelectedVessel(__instance, out _, out string clearError);
            if (!string.IsNullOrEmpty(clearError))
                ParsekLog.Warn("GhostMap", $"Failed to clear Tracking Station ghost selection after Fly block: {clearError}");

            // Release the input lock that selecting the vessel may have set.
            // The original FlyVessel transitions to flight scene (clearing locks implicitly).
            // Since we skip the original, we must dismiss ourselves to avoid trapping
            // the user in tracking station with a stale input lock.
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnVesselDeleteConfirm")]
    internal static class GhostTrackingDeletePatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null)
            {
                // Null here means either no vessel selected (normal) or the private field was
                // renamed in a KSP update (Traverse failed silently). Log a warning so we notice.
                ParsekLog.Warn("GhostMap", "GhostTrackingDeletePatch: selectedVessel is null — Traverse may have failed");
                return true;
            }
            if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))
                return true;

            GhostTrackingStationSelection.SelectGhost(selected, "Delete block");
            ScreenMessages.PostScreenMessage(
                $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be deleted. " +
                "It will be removed automatically when its chain resolves.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked Delete for ghost '{selected.vesselName}' pid={selected.persistentId}");

            GhostTrackingStationSelection.TryClearSelectedVessel(__instance, out _, out string clearError);
            if (!string.IsNullOrEmpty(clearError))
                ParsekLog.Warn("GhostMap", $"Failed to clear Tracking Station ghost selection after Delete block: {clearError}");

            // Release the input lock that the confirmation dialog set.
            // The original OnVesselDeleteConfirm calls OnDialogDismiss which unlocks UI.
            // Since we skip the original, we must dismiss ourselves.
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }

    /// <summary>
    /// Prevents SpaceTracking.SetVessel from selecting ghost ProtoVessels.
    /// SetVessel enables the Fly/Delete/Recover buttons for the selected vessel.
    /// If we only block SetVessel, the buttons stay enabled from the previous
    /// selection — clicking Fly would then fly to the wrong vessel (e.g., an
    /// asteroid). We must also lock the buttons after blocking.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "SetVessel")]
    internal static class GhostTrackingSetVesselPatch
    {
        static bool Prefix(SpaceTracking __instance, Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            GhostTrackingStationSelection.SelectGhost(v, "SetVessel block");
            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost — it shows the predicted orbit of a recorded vessel.",
                5f, ScreenMessageStyle.UPPER_CENTER);

            // Disable Fly/Delete/Recover buttons so the user can't accidentally
            // act on whatever vessel was previously selected internally.
            __instance.FlyButton.interactable = false;
            __instance.DeleteButton.interactable = false;
            __instance.RecoverButton.interactable = false;

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(__instance, out object previousSelection, out string clearError);
            if (!string.IsNullOrEmpty(clearError))
                ParsekLog.Warn("GhostMap", $"Failed to clear Tracking Station ghost selection after SetVessel block: {clearError}");

            ParsekLog.Info("GhostMap",
                $"Blocked SetVessel for ghost '{v.vesselName}' pid={v.persistentId} in Tracking Station " +
                $"clearedSelection={cleared} hadPreviousSelection={previousSelection != null}");
            return false;
        }

        static void Postfix(Vessel v)
        {
            if (v == null)
            {
                GhostTrackingStationSelection.ClearSelectedGhost("stock SetVessel(null)");
                return;
            }

            if (!GhostMapPresence.IsGhostMapVessel(v.persistentId))
            {
                GhostTrackingStationSelection.ClearSelectedGhost(
                    $"stock SetVessel '{v.vesselName}' pid={v.persistentId}");
            }
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnRecoverConfirm")]
    internal static class GhostTrackingRecoverPatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null)
            {
                ParsekLog.Warn("GhostMap", "GhostTrackingRecoverPatch: selectedVessel is null — Traverse may have failed");
                return true;
            }
            if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))
                return true;

            GhostTrackingStationSelection.SelectGhost(selected, "Recover block");
            ScreenMessages.PostScreenMessage(
                $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be recovered. " +
                "It will be removed automatically when its chain resolves.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked Recover for ghost '{selected.vesselName}' pid={selected.persistentId}");

            GhostTrackingStationSelection.TryClearSelectedVessel(__instance, out _, out string clearError);
            if (!string.IsNullOrEmpty(clearError))
                ParsekLog.Warn("GhostMap", $"Failed to clear Tracking Station ghost selection after Recover block: {clearError}");

            // Release input lock (same reason as Delete patch above)
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }

    internal static class GhostTrackingStationSelection
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        private static TrackingStationGhostSelectionInfo selectedGhost;
        private static bool hasSelectedGhost;

        internal static bool HasSelectedGhost => hasSelectedGhost;

        internal static TrackingStationGhostSelectionInfo SelectedGhost => selectedGhost;

        internal static void SelectGhost(Vessel vessel, string source)
        {
            if (vessel == null)
                return;

            int recordingIndex = GhostMapPresence.FindRecordingIndexByVesselPid(vessel.persistentId);
            string recordingId = GhostMapPresence.FindRecordingIdByVesselPid(vessel.persistentId);
            Recording rec = TryGetRecording(recordingId, recordingIndex);
            if (string.IsNullOrEmpty(recordingId))
                recordingId = rec?.RecordingId;
            selectedGhost = new TrackingStationGhostSelectionInfo(
                vessel.persistentId,
                vessel.vesselName,
                recordingIndex,
                recordingId,
                rec != null ? rec.StartUT : double.NaN,
                rec != null ? rec.EndUT : double.NaN,
                rec?.TerminalStateValue,
                rec != null && rec.VesselSpawned,
                rec != null ? rec.SpawnedVesselPersistentId : 0u,
                rec != null);
            hasSelectedGhost = true;

            ParsekLog.Info("GhostMap",
                string.Format(ic,
                    "Selected Tracking Station ghost '{0}' pid={1} recIndex={2} recId={3} source={4}",
                    selectedGhost.VesselName,
                    selectedGhost.GhostPid,
                    selectedGhost.RecordingIndex,
                    selectedGhost.RecordingId ?? "(none)",
                    source ?? "(unknown)"));
        }

        internal static void ClearSelectedGhost(string reason)
        {
            if (!hasSelectedGhost)
                return;

            ParsekLog.Verbose("GhostMap",
                string.Format(ic,
                    "Cleared Tracking Station ghost selection '{0}' pid={1} reason={2}",
                    selectedGhost.VesselName,
                    selectedGhost.GhostPid,
                    reason ?? "(none)"));
            selectedGhost = default;
            hasSelectedGhost = false;
        }

        internal static TrackingStationGhostActionContext BuildActionContext(
            TrackingStationGhostSelectionInfo selection,
            bool hasGhostVessel,
            bool canFocus,
            bool canSetTarget,
            double currentUT,
            Dictionary<uint, GhostChain> chains)
        {
            bool hasRecording = TryResolveRecording(
                selection,
                out Recording rec,
                out int recordingIndex);
            bool alreadyMaterialized = GhostMapPresence.IsTrackingStationRecordingAlreadyMaterialized(rec);
            var materialize = hasRecording
                ? GhostMapPresence.ShouldSpawnAtTrackingStationEnd(rec, currentUT, chains)
                : (needsSpawn: false, reason: "no-recording");

            return new TrackingStationGhostActionContext(
                hasGhostVessel,
                canFocus,
                canSetTarget,
                recordingIndex,
                hasRecording,
                materialize.needsSpawn,
                materialize.reason,
                alreadyMaterialized);
        }

        internal static void SetSelectedGhostForTesting(TrackingStationGhostSelectionInfo selection)
        {
            selectedGhost = selection;
            hasSelectedGhost = selection.GhostPid != 0;
        }

        internal static void ClearSelectedGhostForTesting()
        {
            selectedGhost = default;
            hasSelectedGhost = false;
        }

        private static Recording TryGetRecording(string recordingId, int recordingIndex)
        {
            if (!string.IsNullOrEmpty(recordingId))
            {
                return GhostMapPresence.TryGetCommittedRecordingById(
                    recordingId,
                    out _,
                    out Recording recording)
                    ? recording
                    : null;
            }

            return GhostMapPresence.GetCommittedRecordingByRawIndex(recordingIndex);
        }

        private static bool TryResolveRecording(
            TrackingStationGhostSelectionInfo selection,
            out Recording recording,
            out int recordingIndex)
        {
            return GhostMapPresence.TryGetCommittedRecordingById(
                selection.RecordingId,
                out recordingIndex,
                out recording);
        }

        internal static bool TryClearSelectedVessel(object trackingInstance, out object previousSelection, out string error)
        {
            previousSelection = null;
            error = null;

            if (trackingInstance == null)
                return false;

            try
            {
                FieldInfo selectedField = trackingInstance.GetType().GetField(
                    "selectedVessel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (selectedField == null)
                {
                    error = "selectedVessel field not found";
                    return false;
                }

                previousSelection = selectedField.GetValue(trackingInstance);
                if (previousSelection == null)
                    return false;

                error = TryClearPreviousSelectionArtifacts(previousSelection);
                selectedField.SetValue(trackingInstance, null);

                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        // Mirror the deselection-only part of SpaceTracking.SetVessel without
        // its Tracking Station tab-switch side effects in mission modes.
        private static string TryClearPreviousSelectionArtifacts(object previousSelection)
        {
            Vessel previousVessel = previousSelection as Vessel;
            if (previousVessel == null)
                return null;

            try
            {
                if (previousVessel.orbitRenderer != null)
                {
                    previousVessel.orbitRenderer.isFocused = false;
                    previousVessel.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                }

                previousVessel.DetachPatchedConicsSolver();
                return null;
            }
            catch (Exception ex)
            {
                return $"selectedVessel cleanup failed: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
