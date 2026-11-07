using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Recording start: rewind save, start location and launch site, start flags and snapshots,
    // tree backstop, route-origin dock proof and cargo manifest.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Captures a quicksave for rewind at recording start. Saves via KSP API
        /// (which writes to root saves dir), then moves to Parsek/Saves/ subdirectory.
        /// </summary>
        /// <summary>
        /// Releases the rewind save of a previous aborted recording before a new one is
        /// captured. The file is not deleted here: a save written meanwhile may still name it,
        /// so <see cref="ParsekFileCollector"/> deletes it once nothing names it
        /// (design-load-restores-the-save.md 4.5). Clears <see cref="RewindSaveFileName"/>.
        /// </summary>
        private void CleanupOrphanedRewindSave()
        {
            if (string.IsNullOrEmpty(RewindSaveFileName))
                return;

            ParsekLog.Info("Recorder",
                $"Released orphaned rewind save for collection: {RewindSaveFileName}");
            RewindSaveFileName = null;
        }

        private void CaptureRewindSave(Vessel v, bool isPromotion)
        {
            if (isPromotion)
            {
                ParsekLog.Verbose("Recorder", "Rewind save skipped: tree branch/chain promotion");
                return;
            }

            // Clean up orphaned rewind save from a previous aborted recording
            CleanupOrphanedRewindSave();

            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            string saveFileName = $"parsek_rw_{shortId}";

            try
            {
                // Save to root saves dir (only KSP API path available)
                string result = GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: SaveGame returned null");
                    return;
                }

                // Move from root to Parsek/Saves/
                string savesDir = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string rootPath = System.IO.Path.Combine(savesDir, saveFileName + ".sfs");

                string rewindDir = RecordingPaths.EnsureRewindSavesDirectory();
                if (string.IsNullOrEmpty(rewindDir))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: cannot create Parsek/Saves/ directory");
                    try { System.IO.File.Delete(rootPath); } catch { }
                    return;
                }

                string destPath = System.IO.Path.Combine(rewindDir, saveFileName + ".sfs");
                System.IO.File.Move(rootPath, destPath);

                // SaveGame also wrote a .loadmeta sidecar to the root; only the .sfs
                // moves, so delete the orphan instead of leaving it to litter the save folder.
                FileIOUtils.DeleteSaveSidecarLoadMeta(savesDir, saveFileName, "Recorder");

                RewindSaveFileName = saveFileName;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Recorder", $"Failed to capture rewind save: {ex.Message}");
                return;
            }

            RewindReservedFunds = 0;
            RewindReservedScience = 0;
            RewindReservedRep = 0;

            ParsekLog.Info("Recorder",
                $"Captured rewind save at UT {Planetarium.GetUniversalTime()}: vessel \"{v.vesselName}\" in {v.situation} (save: {saveFileName})");
        }

        internal static bool ShouldShowStartRecordingScreenMessage(
            bool isPromotion,
            bool suppressStartScreenMessage)
        {
            return !isPromotion && !suppressStartScreenMessage;
        }

        // Pass suppressStartScreenMessage=true for fresh, non-continuation starts where
        // the caller posts its own custom screen message.
        public void StartRecording(bool isPromotion = false, bool suppressStartScreenMessage = false)
        {
            if (Time.timeScale < 0.01f)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording while paused");
                ParsekLog.ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording: no active vessel");
                return;
            }

            Recording.Clear();
            OrbitSegments.Clear();
            PartEvents.Clear();
            FlagEvents.Clear();
            SegmentEvents.Clear();
            // PrepareQuickloadResumeStateIfNeeded must run BEFORE
            // ResetPartEventTrackingState so the chain-promotion empty-engine gate
            // (inside ResetPartEventTrackingState) sees the POST-TRIM active recording.
            // If the gate runs first, it can count engine events from the abandoned
            // future (state recorded between the quicksave UT and the live UT at
            // load time), skip sentinel emission, and then PrepareQuickloadResumeStateIfNeeded
            // trims those future events away — leaving the resumed recording with zero
            // engine events and re-tripping the playback orphan-engine auto-start.
            // The two helpers are otherwise independent: PrepareQuickloadResumeStateIfNeeded
            // touches only the tree's active recording (Points/PartEvents/etc. past
            // cutoffUT) and the recorder's pendingRestoreEnvironmentResync flag, while
            // ResetPartEventTrackingState rebuilds in-memory tracking sets from the
            // current live vessel.
            PrepareQuickloadResumeStateIfNeeded();
            ResetPartEventTrackingState(v, emitSeedEvents: !isPromotion);

            LogVisualRecordingCoverage(v);

            if (!isPromotion)
                CapturePreLaunchResources();

            // Capture rewind save (quicksave stored in Parsek/Saves/)
            CaptureRewindSave(v, isPromotion);

            InitializeRecordingFlags(v);
            CaptureStartLocation(v, isPromotion);
            var initialEnv = InitializeEnvironmentAndAnchorTracking(v);
            CaptureStartSnapshots(v);
            // M2 harvest capture (plan D5 part i): converters already running at
            // recording start open a window AT start. Runs on every start flavor
            // (windows are independent witnesses; a BG-voided manifest just
            // degrades the leg to legacy analysis regardless).
            InitializeHarvestWindowAtStart(v);
            ArmReFlyPostLoadSettleIfNeeded(v, isPromotion);

            // Check if vessel is already on rails (e.g. started recording during time warp)
            if (v.packed)
            {
                if (reFlyPostLoadSettleActive
                    && ShouldSkipOrbitSegmentForAtmosphere(v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
                {
                    // The low-level trajectory write gate would suppress this too;
                    // keep the packed-start skip visible as a one-shot Info log.
                    ParsekLog.Info("Recorder",
                        $"Re-Fly post-load settle skipped packed atmospheric start boundary sample " +
                        $"(alt={v.altitude:F0}, atmoDepth={v.mainBody.atmosphereDepth:F0})");
                }
                else
                {
                    // Take one boundary point first
                    SamplePosition(v);
                }

                // Skip orbit segment if below atmosphere — Keplerian orbit ignores drag
                if (ShouldSkipOrbitSegmentForAtmosphere(v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
                {
                    ParsekLog.Info("Recorder",
                        $"Recording started on rails in atmosphere — skipping orbit segment " +
                        $"(alt={v.altitude:F0}, atmoDepth={v.mainBody.atmosphereDepth:F0})");
                }
                else
                {
                    InitializeOnRailsOrbitSegment(v, initialEnv);
                }
            }

            // Register the Harmony patch to call us each physics frame
            Patches.PhysicsFramePatch.ActiveRecorder = this;

            SubscribePartEvents();

            // Initialize diagnostics growth rate tracking
            DiagnosticsState.activeGrowthRate = default;
            DiagnosticsState.hasActiveGrowthRate = true;
            BootstrapAvgBytesPerPoint();

            int partCount = v.parts != null ? v.parts.Count : 0;
            string treeRecDbg = "-";
            if (ActiveTree != null && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings != null
                && ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out var activeTreeRec)
                && activeTreeRec != null)
            {
                treeRecDbg = activeTreeRec.DebugName;
            }
            ParsekLog.Info("Recorder",
                string.Format(CultureInfo.InvariantCulture,
                    "Recording started: vessel=\"{0}\", parts={1}, points=0{2}, treeRec={3}",
                    v.vesselName, partCount, isPromotion ? ", promotion" : "", treeRecDbg));
            NoteGravityHack("record-start", GravityHackDetector.ReadLiveHackedFactor());
            if (ShouldShowStartRecordingScreenMessage(isPromotion, suppressStartScreenMessage))
                ParsekLog.ScreenMessage("Recording STARTED", 2f);
        }

        /// <summary>
        /// Captures the pre-launch resource snapshot (funds, science, reputation) for delta
        /// tracking. Skipped for promotion — resources belong to the tree root.
        /// </summary>
        private void CapturePreLaunchResources()
        {
            PreLaunchFunds = 0;
            PreLaunchScience = 0;
            PreLaunchReputation = 0;
            try
            {
                if (Funding.Instance != null)
                    PreLaunchFunds = Funding.Instance.Funds;
                if (ResearchAndDevelopment.Instance != null)
                    PreLaunchScience = ResearchAndDevelopment.Instance.Science;
                if (Reputation.Instance != null)
                    PreLaunchReputation = Reputation.Instance.reputation;
            }
            catch (Exception ex) { ParsekLog.Verbose("Recorder", $"Pre-launch resource capture failed: {ex.Message}"); }
            ParsekLog.Verbose("Recorder",
                $"Pre-launch resources captured: funds={PreLaunchFunds:F0}, science={PreLaunchScience:F1}, rep={PreLaunchReputation:F1}");
        }

        /// <summary>
        /// Captures start location context (Phase 10): body, biome, situation, and launch site.
        /// </summary>
        private void CaptureStartLocation(Vessel v, bool isPromotion)
        {
            StartBodyName = v.mainBody?.name;
            StartSituation = v.isEVA ? "EVA" : LiveVesselSnapshot.HumanizeSituation(v.situation);
            StartBiome = LiveVesselSnapshot.TryResolveBiome(v.mainBody?.name, v.latitude, v.longitude);
            uint freshRolloutPid = SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid;
            string landedAtSite = ResolveLandedAtLaunchSite(v.situation, v.landedAt);
            string launchSiteGate;
            bool captureSite = ShouldCaptureLaunchSite(
                v.isEVA,
                isPromotion,
                v.situation,
                StartedFromPrelaunchTransition,
                freshRolloutPid != 0u && v.persistentId == freshRolloutPid,
                landedAtSite != null,
                out launchSiteGate);
            // A vessel standing on a site names it itself (landedAt is live, the
            // FlightDriver static is only as fresh as the last new launch).
            LaunchSiteName = !captureSite
                ? null
                : landedAtSite != null
                    ? HumanizeLaunchSiteName(landedAtSite)
                    : ResolveLaunchSiteName(v, isPromotion);
            ParsekLog.Verbose("Recorder",
                $"Start location captured: body={StartBodyName ?? "(null)"}, biome={StartBiome ?? "(null)"}, " +
                $"situation={StartSituation ?? "(null)"}, launchSite={LaunchSiteName ?? "(null)"}, " +
                $"launchSiteGate={launchSiteGate}");
        }

        /// <summary>
        /// Whether a recording start may carry <c>FlightDriver.LaunchSiteName</c>. That field
        /// is static and only <c>FlightDriver.StartWithNewLaunch</c> writes it
        /// (<c>StartAndFocusVessel</c> never resets it, decompiled KSP 1.12.5), so after a
        /// save load, a Tracking Station Fly, a vessel switch or a later take-off it still
        /// names the LAST launch (default "LaunchPad"). A non-empty site is the whole
        /// KSC-origin proof (<c>RouteAnalysisEngine.IsKscOriginRecording</c>), so a stale
        /// one bills a route as a KSC launch. Captured only for a launch start: the vessel
        /// is PRELAUNCH now, the start was the PRELAUNCH -> flight auto-record transition,
        /// the vessel is this scene's fresh rollout, or it is standing on a stock launch site
        /// (a wheeled runway rollout is LANDED, never PRELAUNCH, and stays so after a revert
        /// or reload). EVA and promotion / continuation starts never carry a site.
        /// </summary>
        internal static bool ShouldCaptureLaunchSite(
            bool isEva,
            bool isPromotionOrContinuation,
            Vessel.Situations situation,
            bool fromPrelaunchTransition,
            bool isFreshRolloutVessel,
            bool standingOnLaunchSite,
            out string reason)
        {
            if (isEva)
            {
                reason = "eva";
                return false;
            }
            if (isPromotionOrContinuation)
            {
                reason = "promotion-or-continuation";
                return false;
            }
            if (situation == Vessel.Situations.PRELAUNCH)
            {
                reason = "prelaunch";
                return true;
            }
            if (fromPrelaunchTransition)
            {
                reason = "launch-transition";
                return true;
            }
            if (isFreshRolloutVessel)
            {
                reason = "fresh-rollout";
                return true;
            }
            if (standingOnLaunchSite)
            {
                reason = "landed-at-launch-site";
                return true;
            }
            reason = "not-a-launch-start";
            return false;
        }

        /// <summary>
        /// The stock launch-site name a vessel at rest is standing on (its live
        /// <c>landedAt</c>), or null when it is not at rest on one.
        /// </summary>
        internal static string ResolveLandedAtLaunchSite(Vessel.Situations situation, string landedAt)
        {
            if (situation != Vessel.Situations.PRELAUNCH
                && situation != Vessel.Situations.LANDED
                && situation != Vessel.Situations.SPLASHED)
                return null;
            return LaunchSiteExclusionZones.IsStockLaunchSiteName(landedAt) ? landedAt : null;
        }

        /// <summary>
        /// Resolves the launch site name for the current vessel.
        /// Uses FlightDriver.LaunchSiteName for stock sites (LaunchPad, Runway, Desert Airfield,
        /// Woomerang Launch Site, Island Airfield). The value persists in FlightDriver after
        /// the vessel transitions from PRELAUNCH to FLYING, so we capture it regardless of
        /// current situation. Returns null when the launch site is unknown.
        /// </summary>
        internal static string ResolveLaunchSiteName(Vessel v, bool isPromotion)
        {
            if (v == null) return null;

            // Only capture launch site for the initial launch — not for EVAs,
            // chain continuations, or promoted background recordings.
            // FlightDriver.LaunchSiteName persists from the original launch
            // and would incorrectly tag all subsequent recordings.
            if (v.isEVA || isPromotion) return null;

            try
            {
                string site = FlightDriver.LaunchSiteName;
                if (string.IsNullOrEmpty(site))
                    return null;
                return HumanizeLaunchSiteName(site);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Recorder", $"ResolveLaunchSiteName failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Humanizes stock launch site names. KSP internal names are "LaunchPad", "Runway".
        /// Making History DLC names are already human-readable.
        /// </summary>
        internal static string HumanizeLaunchSiteName(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) return null;
            switch (siteName)
            {
                case "LaunchPad": return "Launch Pad";
                case "Runway":    return "Runway";
                default:
                    // MH DLC uses underscores internally (e.g., "Woomerang_Launch_Site")
                    return siteName.Replace('_', ' ');
            }
        }

        /// <summary>
        /// Pure half of the PersistentRotation detection: the matched spelling when either the
        /// loaded assembly's <paramref name="name"/> or its <paramref name="dllName"/> is one of
        /// <see cref="PersistentRotationAssemblyNames"/> (ordinal, exact), else null. The
        /// assembly name is tried first, so the 1.12 build reports its KSPAssembly name.
        /// </summary>
        internal static string MatchPersistentRotationAssembly(string name, string dllName)
        {
            for (int i = 0; i < PersistentRotationAssemblyNames.Length; i++)
                if (string.Equals(name, PersistentRotationAssemblyNames[i], StringComparison.Ordinal))
                    return PersistentRotationAssemblyNames[i];
            for (int i = 0; i < PersistentRotationAssemblyNames.Length; i++)
                if (string.Equals(dllName, PersistentRotationAssemblyNames[i], StringComparison.Ordinal))
                    return PersistentRotationAssemblyNames[i];
            return null;
        }

        private void InitializeRecordingFlags(Vessel v)
        {
            IsRecording = true;
            isOnRails = false;
            VesselDestroyedDuringRecording = false;
            CaptureAtStop = null;
            TransitionToBackgroundPending = false;
            RecordingVesselId = v.persistentId;
            RecordingStartedAsEva = v.isEVA;
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;
            lastRecordedWorldRotation = Quaternion.identity;
            hasLastRecordedWorldRotation = false;
            LastRecordedAltitude = double.NaN;
            FinalizationCache = null;
            lastFinalizationCacheRefreshUT = double.MinValue;
            highFidelitySamplingUntilUT = double.NaN;
            highFidelitySamplingReason = null;
            reFlyTreeSamplingProximityMeters = double.NaN;
            reFlyTreeSamplingProximitySource = null;
            pendingJointChildPartOriginSeeds.Clear();
            ResetReFlyPostLoadSettle();
            highFidelityProximityVesselPids.Clear();

            // Reset start-docked origin proof so a previous Stop->Start sequence
            // cannot leak a prior recording's partner detection into the new one.
            pendingRouteOriginProof = null;
            pendingRouteOriginProofStartPartPids = null;
            // Same leak guard for the M2 run manifest: re-populated (captured or
            // adopted) in CaptureStartSnapshots.
            pendingRouteRunManifest = null;
            // M2 harvest-window state: windows belong to one recorded leg.
            recorderHarvestWindows.Clear();
            openHarvestWindow = null;
            lastStopClosedHarvestWindow = null;
            harvestRailsExitPollPending = false;
            harvestRailsExitPollArmedUT = double.NaN;

            string persistentRotationMatch = null;
            foreach (var loaded in AssemblyLoader.loadedAssemblies)
            {
                persistentRotationMatch = MatchPersistentRotationAssembly(loaded.name, loaded.dllName);
                if (persistentRotationMatch != null)
                    break;
            }
            hasPersistentRotation = persistentRotationMatch != null;
            ParsekLog.Info("Recorder", $"PersistentRotation mod detected: {hasPersistentRotation}"
                + (persistentRotationMatch != null ? $" (matched={persistentRotationMatch})" : ""));

            // Seed atmosphere state
            wasInAtmosphere = v.mainBody != null && v.mainBody.atmosphere
                && v.altitude < v.mainBody.atmosphereDepth;
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            EnteredAtmosphere = false;
            SoiChangePending = false;
            SoiChangeFromBody = null;
            ClearPhysicsSoiSamplePending();

            // Seed altitude boundary state (airless bodies)
            ReseedAltitudeState(v);

            ParsekLog.Verbose("Recorder", $"Boundary detection initialized: body={v.mainBody?.name}, " +
                $"inAtmo={wasInAtmosphere}, hasAtmo={v.mainBody?.atmosphere}, alt={v.altitude:F0}m" +
                $", altThreshold={currentAltitudeThreshold:F0}m, aboveThreshold={wasAboveAltitudeThreshold}");
        }

        /// <summary>
        /// Initializes environment hysteresis, TrackSections, anchor detection for RELATIVE
        /// frame, and logs the surface-relative rotation. Returns the initial environment
        /// classification (needed by the on-rails orbit segment path).
        /// </summary>
        private SegmentEnvironment InitializeEnvironmentAndAnchorTracking(Vessel v)
        {
            // Initialize environment tracking
            TrackSections.Clear();
            var initialEnv = ClassifyCurrentEnvironment(v);
            environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            StartNewTrackSection(initialEnv, ReferenceFrame.Absolute, Planetarium.GetUniversalTime());

            // Initialize anchor detection for RELATIVE frame (Phase 3a)
            isRelativeMode = false;
            ClearCurrentRecordingAnchor();
            treeVesselPids = BuildTreeVesselPids();
            ParsekLog.Info("Anchor",
                $"Anchor detection initialized: treeVesselPids={treeVesselPids?.Count ?? 0} " +
                $"hasTree={ActiveTree != null}");

            // Log surface-relative rotation for synthetic recording calibration
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info("Recorder",
                $"srfRelRotation=({v.srfRelRotation.x.ToString("R", ic)},{v.srfRelRotation.y.ToString("R", ic)}," +
                $"{v.srfRelRotation.z.ToString("R", ic)},{v.srfRelRotation.w.ToString("R", ic)}) " +
                $"UT={Planetarium.GetUniversalTime().ToString("R", ic)}");

            return initialEnv;
        }

        /// <summary>
        /// Captures the initial backup snapshot, the start manifests and the ghost
        /// visual snapshot. Must be called AFTER InitializeRecordingFlags.
        /// </summary>
        private void CaptureStartSnapshots(Vessel v)
        {
            RefreshBackupSnapshot(v, "record_start", force: true);
            RefreshFinalizationCache(v, "record_start", force: true);
            pendingStartResources = VesselSnapshotOps.ExtractResourceManifest(lastGoodVesselSnapshot);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartResources?.Count ?? 0} start resource type(s)");
            pendingStartInventory = VesselSnapshotOps.ExtractInventoryManifest(lastGoodVesselSnapshot, out pendingStartInventorySlots);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartInventory?.Count ?? 0} start inventory item type(s), {pendingStartInventorySlots} slot(s)");
            pendingStartCrew = VesselSpawner.ExtractCrewManifest(lastGoodVesselSnapshot);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartCrew?.Count ?? 0} start crew trait(s)");
            pendingStartControllers = ControllerInfo.CaptureFromVessel(v);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartControllers?.Count ?? 0} start controller part(s)");
            CaptureStartRouteOriginProofIfDocked(v);
            CaptureRunCargoManifestAtStart(v);

            // Launch-unique identity: capture the live Vessel.id Guid (assigned fresh per launch,
            // unlike the craft-baked persistentId). Falls back to the snapshot's `pid` value when
            // the live vessel is unavailable. Disambiguates relaunches of the same craft.
            pendingRecordedVesselGuid = (v != null && v.id != Guid.Empty)
                ? v.id.ToString("N")
                : VesselLaunchIdentity.TryReadVesselGuid(lastGoodVesselSnapshot);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured launch guid={pendingRecordedVesselGuid ?? "(none)"}");
            initialGhostVisualSnapshot = lastGoodVesselSnapshot != null
                ? lastGoodVesselSnapshot.CreateCopy()
                : FlightRecorder.TryBackupSnapshot(v);
            ApplyStartRecordingTreeBackstop(
                ActiveTree, pendingStartControllers, pendingStartCrew, pendingRecordedVesselGuid,
                StartBodyName, StartBiome, StartSituation, LaunchSiteName,
                initialGhostVisualSnapshot);
        }

        /// <summary>
        /// Backstop: forward the just-captured start identity onto the active tree
        /// recording if it doesn't already carry it. Covers the always-tree-root
        /// path (created with whatever vessel was active at the time of root
        /// creation, which may have been null) and the promotion path
        /// (preexisting tree rec from a legacy recording without Controllers).
        /// No-op when the tree rec was already populated at creation time.
        /// <para>
        /// Every input is already a captured value rather than a live Vessel read, so the
        /// forward is testable without a KSP runtime; StartRecording hands it the fields it
        /// captured a few lines earlier. Returns true when anything was adopted.
        /// </para>
        /// </summary>
        internal static bool ApplyStartRecordingTreeBackstop(
            RecordingTree activeTree,
            List<ControllerInfo> startControllers,
            Dictionary<string, int> startCrew,
            string recordedVesselGuid,
            string startBodyName, string startBiome, string startSituation, string launchSiteName,
            ConfigNode startGhostVisualSnapshot = null)
        {
            if (activeTree == null
                || string.IsNullOrEmpty(activeTree.ActiveRecordingId)
                || activeTree.Recordings == null
                || !activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out var treeRecBackstop)
                || treeRecBackstop == null)
                return false;

            bool backstopChanged = false;
            if (treeRecBackstop.AdoptControllersIfEmpty(startControllers))
            {
                backstopChanged = true;
                ParsekLog.Verbose("Recorder",
                    $"StartRecording: forwarded {startControllers.Count} controller part(s) " +
                    $"to active tree recording '{activeTree.ActiveRecordingId}'");
            }
            // Forward the start-crew manifest too (same always-tree-root gap as controllers):
            // the root recording is created before the crew snapshot exists, so without this it
            // never carries StartCrew even though its vessel snapshot has the crew.
            if (treeRecBackstop.AdoptStartCrewIfEmpty(startCrew))
            {
                backstopChanged = true;
                ParsekLog.Verbose("Recorder",
                    $"StartRecording: forwarded {startCrew.Count} start crew trait(s) " +
                    $"to active tree recording '{activeTree.ActiveRecordingId}'");
            }
            // Forward the launch guid too (same always-tree-root gap): the root recording is
            // created before the vessel snapshot exists, so without this it never carries the
            // launch-unique identity even though its snapshot's `pid` has it.
            if (treeRecBackstop.AdoptRecordedVesselGuidIfEmpty(recordedVesselGuid))
            {
                backstopChanged = true;
                ParsekLog.Verbose("Recorder",
                    $"StartRecording: forwarded launch guid {recordedVesselGuid} " +
                    $"to active tree recording '{activeTree.ActiveRecordingId}'");
            }
            // Forward the start-location fields too (same always-tree-root gap): the recorder
            // captures body / launch site at StartRecording (above), but for a multi-recording
            // tree (launch then dock / continue) the OnSave flush forwards them to whichever
            // child is active at save time, not the root, so the root keeps no LaunchSiteName /
            // StartBodyName and a KSC-origin supply route cannot resolve its origin. A mid-flight
            // child has LaunchSiteName == null so it never adopts a launch site it lacks.
            if (treeRecBackstop.AdoptStartLocationIfEmpty(
                    startBodyName, startBiome, startSituation, launchSiteName))
            {
                backstopChanged = true;
                ParsekLog.Verbose("Recorder",
                    $"StartRecording: forwarded start location (body={startBodyName ?? "(null)"}, " +
                    $"launchSite={launchSiteName ?? "(null)"}) to active tree recording '{activeTree.ActiveRecordingId}'");
            }
            // Forward the start ghost snapshot too: a switch / Fly continuation segment is built
            // before any snapshot exists, and without this the commit fills the gap from the END
            // snapshot, so the segment's ghost shows the vessel as it ended and its crew rows
            // name the crew it ended with.
            if (treeRecBackstop.GhostVisualSnapshot == null && startGhostVisualSnapshot != null)
            {
                treeRecBackstop.GhostVisualSnapshot = startGhostVisualSnapshot.CreateCopy();
                backstopChanged = true;
                ParsekLog.Verbose("Recorder",
                    $"StartRecording: forwarded start ghost snapshot " +
                    $"to active tree recording '{activeTree.ActiveRecordingId}'");
            }
            if (backstopChanged)
                treeRecBackstop.MarkFilesDirty();
            return backstopChanged;
        }

        /// <summary>
        /// Logistics start-docked origin proof producer. Builds the origin-partner candidate
        /// list off the live vessel and delegates the null-snapshot guard, the resolver
        /// dispatch, the per-branch logging, and the proof construction to
        /// <see cref="RouteProofCapture.BuildStartRouteOriginProof"/>. This method's
        /// own responsibility is just the live-Vessel work: null-vessel guard plus
        /// candidate construction from <c>v.parts</c>.
        ///
        /// <para>TWO candidate producers, in this order:</para>
        /// <para>1. SETTLED DOCK SEAMS (<see cref="RouteProofCapture.IsSettledDockSeam"/>).
        /// <c>Part.Couple</c> merges the docked pair into ONE <c>Vessel</c>, so the origin
        /// partner is that merged vessel: its pid, situation and body-fixed coordinates ARE the
        /// docked pair's, and <c>Part.Undock</c> leaves that pid on the half that keeps the
        /// parent side of the seam - the depot, when the transport docked into it. When the pid
        /// no longer resolves at dispatch time the M1 endpoint descriptor carried alongside it
        /// gives <c>RouteEndpointResolver</c> its proximity rebuild.</para>
        /// <para>2. EXTERNALLY PARENTED PARTS (<c>p.parent.vessel != v</c>), the pre-2026-09-02
        /// reading. Kept rather than replaced: it costs one pass over <c>v.parts</c> and it is
        /// the only reading that could see an UNSETTLED coupling, which is the mirror direction
        /// of the fix. It is unsatisfiable once a couple has settled - measured live, H56
        /// <c>2026-09-02_0545</c>: <c>externalParentParts=0</c> on a landed docked pair.</para>
        /// </summary>
        private void CaptureStartRouteOriginProofIfDocked(Vessel v)
        {
            pendingRouteOriginProof = null;
            pendingRouteOriginProofStartPartPids = null;

            // Live-vessel-only guard: candidates can only be built from a live
            // Vessel.parts list, so this guard stays in production rather than
            // moving into the pure helper.
            if (v == null || v.parts == null)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteOriginProof skipped: no live parts at record_start recId={RecordingVesselId} " +
                    $"vessel='{v?.vesselName}'");
                return;
            }

            var candidates = new List<DockSeamPairCandidate>();
            List<RouteProofCapture.SeamPartRecord> seamPartRecords = BuildSeamPartRecords(v);

            // THE ONLY CANDIDATE PRODUCER: settled dock seams. Every seam between the SAME
            // two craft (a two-port dock) collapses to one unordered pair in the resolver, so
            // a single docked pair can never read PartnerAmbiguous.
            //
            // THE PAIR RULE (P12). Each seam is read as a PAIR: the node on this part carries
            // its OWN half's pre-dock identity in `vesselInfo`, and the docked partner part's
            // node carries the OTHER half's. NEITHER half is chosen here. There is no "base"
            // type that matters - bases are ordinary vessels, and a route candidate is defined
            // by transfers and docks - so the origin is bound at the UNDOCK, to the half the
            // RUN was not flying. Nothing here reads which half stock made dominant on
            // the merge either: `v.persistentId` and `v.rootPart` are deliberately NOT identity
            // inputs, because the merged pid names whichever half won `Vessel.GetDominantVessel`
            // (vesselType, then mass, then a guid compare). Derivation:
            // docs/dev/research/origin-proof-partner-identity-memo.md.
            //
            // Each seam ALSO carries its two halves' part sets, derived by cutting the seam
            // edge in the merged part tree - that is how a half's parts are knowable while both
            // halves are one Vessel, and it is what makes the transport manifests
            // transport-scoped once the bind picks a half.
            //
            // The body-fixed descriptor still comes off the merged vessel, and correctly so:
            // while the halves are docked they are in the same place, so the merged vessel's
            // body / coordinates / situation ARE both halves'.
            int settledDockSeamCandidates = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null) continue;
                ModuleDockingNode seamNode = null;
                Part partnerPart = null;
                for (int m = 0; m < p.Modules.Count && seamNode == null; m++)
                {
                    ModuleDockingNode node = p.Modules[m] as ModuleDockingNode;
                    if (node == null) continue;
                    Part resolvedPartner = TryFindPartByFlightIdOnVessel(v, node.dockedPartUId);
                    if (!RouteProofCapture.IsSettledDockSeam(
                            node.vesselInfo != null,
                            node.dockedPartUId,
                            resolvedPartner != null))
                    {
                        continue;
                    }
                    seamNode = node;
                    partnerPart = resolvedPartner;
                }
                if (seamNode == null) continue;
                settledDockSeamCandidates++;

                DockSeamHalfIdentity near = BuildSeamHalfIdentity(seamNode);
                DockSeamHalfIdentity far = BuildSeamHalfIdentity(
                    FindDockingNodeFacingPart(partnerPart, p.flightID));
                DockSeamPairAdmission admission =
                    RouteProofCapture.ClassifyStartDockedSeamPair(near, far);

                // Cut the seam edge to get each half's own part set on the merged vessel.
                // Fail-closed: an unsplittable seam yields null sets, which the resolver still
                // accepts as a pair (the identity is intact) but the bind then reads as
                // NoPairPending - no wrong scope is ever invented.
                bool split = RouteProofCapture.TrySplitPartsAcrossSeam(
                    seamPartRecords,
                    p.flightID,
                    partnerPart != null ? partnerPart.flightID : 0u,
                    out List<uint> nearSidePids,
                    out List<uint> farSidePids);

                ParsekLog.Verbose("Recorder",
                    $"RouteOriginProof seam pair: recId={RecordingVesselId} " +
                    $"seamPart={p.flightID.ToString(CultureInfo.InvariantCulture)} " +
                    $"nearName='{near.VesselName ?? "<none>"}' nearType={near.VesselType.ToString(CultureInfo.InvariantCulture)} " +
                    $"nearRoot={near.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"farName='{far.VesselName ?? "<none>"}' farType={far.VesselType.ToString(CultureInfo.InvariantCulture)} " +
                    $"farRoot={far.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"admission={admission} split={(split ? "1" : "0")} " +
                    $"nearParts={nearSidePids?.Count ?? 0} farParts={farSidePids?.Count ?? 0}");
                if (admission != DockSeamPairAdmission.Admitted)
                    continue;

                candidates.Add(new DockSeamPairCandidate(
                    p.persistentId,
                    near,
                    far,
                    nearSidePids,
                    farSidePids,
                    (int)v.situation,
                    v.mainBody != null ? v.mainBody.bodyName : null,
                    v.latitude,
                    v.longitude,
                    v.altitude));
            }

            // THE RETIRED READING, COUNTED AND NOT EMITTED. `p.parent.vessel != v` is the
            // pre-2026-09-02 candidate rule. It produced no candidate on either host that
            // has ever measured it (H55 `externalParentParts=0`, H56 the same on a landed
            // docked pair), so it emits nothing here. NO MECHANISM IS CLAIMED for when it
            // could be non-zero - an earlier draft of this comment asserted it was "the only
            // reading that could see an unsettled coupling", which was a hypothesis with no
            // measurement and no decompile behind it. The COUNT is kept because it is the
            // instrument: it is what proves a captured proof came from the dock seam and not
            // from this rule. ONE committed lane pins `externalParentCandidates=0` off THIS
            // line - H57. H55 and H56 pin the in-game cell's own `externalParentParts=0`,
            // which is a separate count taken by `RouteDockCaptureMath.IsExternallyParentedPart`
            // over the live part list, not this scan.
            int externalParentCandidates = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                Part parent = p.parent;
                if (parent == null) continue;
                Vessel parentVessel = parent.vessel;
                if (parentVessel == null || parentVessel == v) continue;
                externalParentCandidates++;
            }

            ParsekLog.Verbose("Recorder",
                $"RouteOriginProof seam scan: settledDockSeamCandidates={settledDockSeamCandidates} " +
                $"externalParentCandidates={externalParentCandidates} " +
                $"recId={RecordingVesselId} vessel='{v.vesselName}' mergedPid={v.persistentId}");

            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)v.situation,
                activeVesselIsEva: v.isEVA,
                candidates: candidates,
                settledDockSeamsScanned: settledDockSeamCandidates,
                snapshot: lastGoodVesselSnapshot,
                vesselContext: v.vesselName,
                recordingVesselId: RecordingVesselId,
                out pendingRouteOriginProof,
                out pendingRouteOriginProofStartPartPids);
        }

        /// <summary>
        /// Resolves a part by KSP <c>flightID</c> WITHIN one vessel. Deliberately not
        /// <c>FlightGlobals.FindPartByID</c>, which searches every loaded vessel: the
        /// settled-dock seam predicate needs "the docked partner part is on THIS vessel",
        /// and a global hit on a partner that has already undocked would keep a dead seam
        /// alive. Returns null when <paramref name="flightId"/> is 0 or unmatched.
        /// </summary>
        private static Part TryFindPartByFlightIdOnVessel(Vessel v, uint flightId)
        {
            if (v == null || v.parts == null || flightId == 0)
                return null;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p != null && p.flightID == flightId)
                    return p;
            }
            return null;
        }

        /// <summary>
        /// The docking node on <paramref name="partnerPart"/> that points BACK at the seam
        /// part (<paramref name="facingFlightId"/>). A part can carry several
        /// <c>ModuleDockingNode</c>s (multi-port adapters, and the stock shielded ports),
        /// and only the one whose <c>dockedPartUId</c> names our side of the seam describes
        /// THIS seam's far half.
        ///
        /// <para>NO FALL-OPEN. An earlier draft returned the first node carrying any
        /// <c>vesselInfo</c> when none named us; on a multi-port adapter that node is
        /// ANOTHER seam's far half, so the origin rule would have been handed a third
        /// vessel's identity and could have named it the depot. Returning null instead makes
        /// <see cref="RouteProofCapture.SelectStartDockedOriginHalf"/> read
        /// HalfIdentityMissing and the seam contribute no candidate - fail closed, which for
        /// this producer means "no proof", never "a proof about the wrong craft".</para>
        /// </summary>
        private static ModuleDockingNode FindDockingNodeFacingPart(Part partnerPart, uint facingFlightId)
        {
            if (partnerPart == null || partnerPart.Modules == null)
                return null;

            // Collect the docking nodes in module order, then let the pure selector decide.
            // The live half here is only the module walk; the DECISION - including the
            // fail-closed answer on a multi-port adapter - is pinned by
            // RouteProofCapture.SelectFacingSeamNodeIndex's own tests.
            var nodes = new List<ModuleDockingNode>();
            var records = new List<RouteProofCapture.SeamNodeRecord>();
            for (int m = 0; m < partnerPart.Modules.Count; m++)
            {
                ModuleDockingNode node = partnerPart.Modules[m] as ModuleDockingNode;
                if (node == null) continue;
                nodes.Add(node);
                records.Add(new RouteProofCapture.SeamNodeRecord(
                    node.vesselInfo != null, node.dockedPartUId));
            }

            int index = RouteProofCapture.SelectFacingSeamNodeIndex(records, facingFlightId);
            return index >= 0 ? nodes[index] : null;
        }

        /// <summary>
        /// Lifts one docking node's <c>DockedVesselInfo</c> into the pure
        /// <see cref="DockSeamHalfIdentity"/> the partner rule reads. A null node or a node
        /// without <c>vesselInfo</c> becomes the not-present identity.
        /// </summary>
        /// <summary>
        /// Reduces the merged vessel's live part list to the pure
        /// <see cref="RouteProofCapture.SeamPartRecord"/> shape the seam splitter reads:
        /// flightID, persistentId, and the INDEX of each part's parent in the same list
        /// (-1 when the parent is absent, i.e. the root). Index-based so the decision stays
        /// headless - xUnit cannot construct a <c>Part</c>.
        /// </summary>
        private static List<RouteProofCapture.SeamPartRecord> BuildSeamPartRecords(Vessel v)
        {
            if (v == null || v.parts == null) return null;
            var indexByPart = new Dictionary<Part, int>(v.parts.Count);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p != null && !indexByPart.ContainsKey(p))
                    indexByPart[p] = i;
            }
            var records = new List<RouteProofCapture.SeamPartRecord>(v.parts.Count);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null)
                {
                    records.Add(new RouteProofCapture.SeamPartRecord(0u, 0u, -1));
                    continue;
                }
                int parentIndex = -1;
                if (p.parent != null && indexByPart.TryGetValue(p.parent, out int found))
                    parentIndex = found;
                records.Add(new RouteProofCapture.SeamPartRecord(
                    p.flightID, p.persistentId, parentIndex));
            }
            return records;
        }

        private static DockSeamHalfIdentity BuildSeamHalfIdentity(ModuleDockingNode node)
        {
            DockedVesselInfo info = node != null ? node.vesselInfo : null;
            if (info == null)
                return new DockSeamHalfIdentity(false, null, -1, 0u);
            return new DockSeamHalfIdentity(true, info.name, (int)info.vesselType, info.rootPartUId);
        }

        /// <summary>
        /// M2 run-manifest START half producer (plan D3). Runs at every
        /// recorder start; the BIRTH discriminator
        /// (<see cref="RouteProofCapture.ShouldCaptureRunManifestStartHalf"/>)
        /// decides whether this start is a genuine recording birth (capture +
        /// write-once onto the tree recording immediately) or a re-bind of an
        /// existing recording (quickload resume: ADOPT the persisted start half
        /// into the pending copy so the eventual active stop can still complete
        /// the END half; BG-promotion: manifest was voided at the background
        /// transition, nothing to adopt - the leg degrades to legacy analysis).
        /// </summary>
        private void CaptureRunCargoManifestAtStart(Vessel v)
        {
            pendingRouteRunManifest = null;

            Recording treeRec = null;
            if (ActiveTree != null
                && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings != null)
            {
                ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out treeRec);
            }

            if (!RouteProofCapture.ShouldCaptureRunManifestStartHalf(treeRec, out string skipReason))
            {
                if (treeRec != null
                    && treeRec.RouteRunManifest != null
                    && treeRec.RouteRunManifest.HasStartHalf)
                {
                    // Write-once: the recording was born earlier and its START
                    // half is already persisted (quickload resume / re-bind).
                    // Adopt a working clone so the END half can still complete
                    // at the eventual active stop.
                    pendingRouteRunManifest = treeRec.RouteRunManifest.DeepClone();
                    ParsekLog.Verbose("Recorder",
                        $"RouteRunManifest start adopted (write-once): recording={treeRec.RecordingId} " +
                        $"parts={pendingRouteRunManifest.TransportPartPersistentIds?.Count ?? 0} " +
                        $"res={pendingRouteRunManifest.StartTransportResources?.Count ?? 0} " +
                        $"endCaptured={(pendingRouteRunManifest.EndCaptured ? "1" : "0")}");
                }
                else
                {
                    ParsekLog.Verbose("Recorder",
                        $"RouteRunManifest start skipped: reason={skipReason} " +
                        $"recId={RecordingVesselId} treeRec={treeRec?.RecordingId ?? "(none)"} " +
                        $"vessel='{v?.vesselName}'");
                }
                return;
            }

            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                lastGoodVesselSnapshot,
                vesselContext: v?.vesselName,
                recordingVesselId: RecordingVesselId);
            if (manifest == null)
                return; // skip reason logged inside the builder

            pendingRouteRunManifest = manifest;
            // Write-once onto the tree recording IMMEDIATELY (plan D3 rule 2):
            // the start half must survive scene exits / quickloads even if this
            // recorder never reaches an active stop.
            treeRec.RouteRunManifest = manifest.DeepClone();
            treeRec.MarkFilesDirty();
            ParsekLog.Verbose("Recorder",
                $"RouteRunManifest start written to tree recording: recording={treeRec.RecordingId} " +
                $"parts={manifest.TransportPartPersistentIds.Count} " +
                $"res={manifest.StartTransportResources?.Count ?? 0}");
        }
    }
}
