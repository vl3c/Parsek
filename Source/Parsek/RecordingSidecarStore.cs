using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    internal static class RecordingSidecarStore
    {
        internal static bool SaveRecordingFiles(Recording rec, bool incrementEpoch = true)
        {
            if (rec == null)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("RecordingStore",
                        $"SaveRecordingFiles called with null recording saveFolder='{SafeSaveFolderForSidecarLog()}'");
                return false;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("RecordingStore",
                        $"SaveRecordingFiles rejected invalid recording id '{rec.RecordingId}' " +
                        $"saveFolder='{SafeSaveFolderForSidecarLog()}'");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureRecordingsDirectory();
                if (dir == null)
                {
                    if (!RecordingStore.SuppressLogging)
                        ParsekLog.Warn("RecordingStore",
                            $"SaveRecordingFiles could not resolve recordings directory " +
                            $"{FormatSidecarContext(rec)}");
                    return false;
                }

                // Save .prec trajectory file
                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(precPath))
                {
                    if (!RecordingStore.SuppressLogging)
                        ParsekLog.Warn("RecordingStore",
                            $"SaveRecordingFiles could not resolve trajectory path " +
                            $"{FormatSidecarContext(rec)} fileKind=trajectory");
                    return false;
                }

                // Save _vessel.craft (always rewrite — snapshot can be mutated by spawn offset)
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(vesselPath))
                {
                    if (!RecordingStore.SuppressLogging)
                        ParsekLog.Warn("RecordingStore",
                            $"SaveRecordingFiles could not resolve vessel snapshot path " +
                            $"{FormatSidecarContext(rec)} fileKind=vessel");
                    return false;
                }
                // Bug #278 follow-up (PR #177, defense-in-depth): do NOT delete an
                // existing _vessel.craft when in-memory VesselSnapshot is null. PR
                // #176's #278 fix routes FinalizePendingLimboTreeForRevert through
                // FinalizeIndividualRecording per leaf, which still hits the
                // defensive null at ParsekFlight.cs:5810 ("rec.VesselSnapshot = null"
                // when the vessel pid lookup fails) for vessel-gone debris. The
                // auto-unreserve-crew pass at ParsekScenario.cs:1131-1140 also nulls
                // the snapshot after the spawn window closes. Both leave the recording
                // with a transient in-memory null while the on-disk sidecar (written
                // earlier by PersistFinalizedRecording from PR #167's #280 fix) is
                // intact. The previous behavior — destructively delete the sidecar —
                // would race with these null-out sites and destroy persisted data on
                // the next OnSave. Stale-cleanup is the responsibility of explicit
                // recording-deletion paths (DeleteRecordingFiles), not of every save.

                // Save _ghost.craft only when it carries data distinct from _vessel.craft.
                string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                return SaveRecordingFilesToPathsInternal(rec, precPath, vesselPath, ghostPath, incrementEpoch);
            }
            catch (Exception ex)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("RecordingStore",
                        $"SaveRecordingFiles failed before staging {FormatSidecarContext(rec)} " +
                        $"ex={FormatExceptionForSidecarLog(ex)}");
                return false;
            }
        }

        internal static void ClearSidecarLoadFailure(Recording rec)
        {
            if (rec == null)
                return;

            rec.SidecarLoadFailed = false;
            rec.SidecarLoadFailureReason = null;
        }

        internal static void MarkSidecarLoadFailure(Recording rec, string reason)
        {
            if (rec == null)
                return;

            rec.SidecarLoadFailed = true;
            rec.SidecarLoadFailureReason = reason;
        }

        internal static bool LoadRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("RecordingStore", "LoadRecordingFiles called with null recording");
                return false;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("RecordingStore", $"LoadRecordingFiles rejected invalid recording id '{rec.RecordingId}'");
                return false;
            }

            ClearSidecarLoadFailure(rec);
            string precPath = null;
            string vesselPath = null;
            string ghostPath = null;
            try
            {
                precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                return LoadRecordingFilesFromPathsInternal(rec, precPath, vesselPath, ghostPath);
            }
            catch (Exception ex)
            {
                MarkSidecarLoadFailure(rec, "exception:" + ex.GetType().Name);
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles failed {FormatSidecarContext(rec)} fileKind=trajectory+snapshots " +
                        $"trajectoryPath='{FormatPathForSidecarLog(precPath)}' " +
                        $"vesselPath='{FormatPathForSidecarLog(vesselPath)}' " +
                        $"ghostPath='{FormatPathForSidecarLog(ghostPath)}' " +
                        $"ex={FormatExceptionForSidecarLog(ex)}");
                }
                return false;
            }
        }

        /// <summary>
        /// Bug #270: Returns true if the sidecar file's epoch doesn't match the
        /// recording's expected epoch (loaded from .sfs). When true, the caller
        /// should skip trajectory deserialization — the .prec is from a different
        /// save point and its data would be inconsistent with the .sfs metadata.
        /// Backward compat: if the .sfs epoch is 0 (old save without epoch),
        /// validation is skipped and the sidecar is always accepted.
        /// </summary>
        internal static bool ShouldSkipStaleSidecar(Recording rec, int fileEpoch)
        {
            if (rec.SidecarEpoch <= 0)
                return false;  // old save without epoch — skip validation

            if (fileEpoch == rec.SidecarEpoch)
                return false;  // epochs match — sidecar is valid

            ParsekLog.Warn("RecordingStore",
                $"Sidecar epoch mismatch {FormatSidecarContext(rec)}: " +
                $".sfs expects epoch {rec.SidecarEpoch}, .prec has epoch {fileEpoch} — " +
                $"sidecar is stale (bug #270), skipping sidecar load (trajectory + snapshots)");
            return true;
        }

        internal static bool LoadRecordingFilesFromPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath)
        {
            if (rec == null)
                throw new ArgumentNullException(nameof(rec));

            ClearSidecarLoadFailure(rec);
            return LoadRecordingFilesFromPathsInternal(rec, precPath, vesselPath, ghostPath);
        }

        private static bool LoadRecordingFilesFromPathsInternal(
            Recording rec, string precPath, string vesselPath, string ghostPath)
        {
            // Load .prec trajectory file
            // ConfigNode.Save writes the node's contents (values + children),
            // and ConfigNode.Load returns a node containing those contents directly.
            if (string.IsNullOrEmpty(precPath) || !File.Exists(precPath))
            {
                MarkSidecarLoadFailure(rec, "trajectory-missing");
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: missing trajectory sidecar {FormatSidecarContext(rec)} " +
                        $"fileKind=trajectory path='{FormatPathForSidecarLog(precPath)}'");
                }
                return false;
            }

            TrajectorySidecarProbe probe;
            if (!RecordingStore.TryProbeTrajectorySidecar(precPath, out probe))
            {
                MarkSidecarLoadFailure(rec, "trajectory-invalid");
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: invalid trajectory sidecar {FormatSidecarContext(rec)} " +
                        $"fileKind=trajectory path='{FormatPathForSidecarLog(precPath)}'");
                }
                return false;
            }
            if (!probe.Supported)
            {
                MarkSidecarLoadFailure(rec, "trajectory-unsupported");
                ParsekLog.Warn("RecordingStore",
                    $"LoadRecordingFiles: unsupported trajectory sidecar {FormatSidecarContext(rec)} " +
                    $"fileKind=trajectory path='{FormatPathForSidecarLog(precPath)}' " +
                    $"encoding={probe.Encoding} version={probe.FormatVersion}");
                return false;
            }

            // Validate recordingId inside file matches
            string fileId = probe.RecordingId;
            if (fileId != null && fileId != rec.RecordingId)
            {
                MarkSidecarLoadFailure(rec, "trajectory-id-mismatch");
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: trajectory recording id mismatch {FormatSidecarContext(rec)} " +
                        $"fileKind=trajectory path='{FormatPathForSidecarLog(precPath)}' fileId='{fileId}'");
                }
                return false;
            }

            // Bug #270: validate sidecar epoch
            if (ShouldSkipStaleSidecar(rec, probe.SidecarEpoch))
            {
                MarkSidecarLoadFailure(rec, "stale-sidecar-epoch");
                return false;
            }

            RecordingStore.DeserializeTrajectorySidecar(precPath, probe, rec);

            // #412: Run legacy-loop migration and degenerate-interval normalization as soon
            // as trajectory points are hydrated, BEFORE snapshot loading. A snapshot-sidecar
            // failure below returns early while leaving Points populated; ParsekScenario.OnLoad
            // still commits the recording, and ParsekKSC treats any enabled recording with
            // >= 2 points as playback-eligible, so waiting until after snapshot success would
            // let a degenerate LoopIntervalSeconds=0 slip past the auto-repair. Both
            // normalizers only touch loop fields + trajectory bounds, so they're safe to run
            // here regardless of snapshot outcome.
            MigrateLegacyLoopIntervalAfterHydration(rec);
            NormalizeDegenerateLoopInterval(rec);

            // #288/#475: eagerly populate TerminalOrbit from the last endpoint-aligned
            // orbit segment when the cache is empty or obviously stale. Without this,
            // GhostMap and spawn consumers can miss or mis-frame orbital end states.
            if (ParsekFlight.ShouldPopulateTerminalOrbitFromLastSegment(rec))
            {
                string bodyBeforePopulate = rec.TerminalOrbitBody;
                ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);
                if (!string.Equals(rec.TerminalOrbitBody, bodyBeforePopulate, StringComparison.Ordinal)
                    && !RecordingStore.SuppressLogging)
                {
                    ParsekLog.Info("RecordingStore", string.Format(CultureInfo.InvariantCulture,
                        "Eager-populated TerminalOrbit for {0} from last orbit segment (body={1}, sma={2:F0})",
                        rec.RecordingId,
                        rec.TerminalOrbitBody,
                        rec.TerminalOrbitSemiMajorAxis));
                }
            }

            RecordingEndpointPhase endpointPhaseBeforeBackfill = rec.EndpointPhase;
            string endpointBodyBeforeBackfill = rec.EndpointBodyName;
            if (RecordingEndpointResolver.BackfillEndpointDecision(rec, "RecordingStore.LoadRecordingFilesFromPathsInternal")
                && (rec.EndpointPhase != endpointPhaseBeforeBackfill
                    || !string.Equals(rec.EndpointBodyName, endpointBodyBeforeBackfill, StringComparison.Ordinal))
                && !RecordingStore.SuppressLogging)
            {
                ParsekLog.Info("RecordingStore", $"Backfilled endpoint decision for {rec.RecordingId} (phase={rec.EndpointPhase}, body={rec.EndpointBodyName ?? "(none)"})");
            }

            // Load snapshot sidecars only after the trajectory probe passes the
            // recording-id and sidecar-epoch safety gates.
            RecordingStore.SnapshotSidecarLoadSummary snapshotSummary = LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath);
            if (!string.IsNullOrEmpty(snapshotSummary.FailureReason))
            {
                MarkSidecarLoadFailure(rec, snapshotSummary.FailureReason);
                return false;
            }

            if (!RecordingStore.SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"LoadRecordingFiles: id={rec.RecordingId} ghostSnapshotMode={rec.GhostSnapshotMode} " +
                    $"hasVesselSnapshot={rec.VesselSnapshot != null} hasGhostSnapshot={rec.GhostVisualSnapshot != null}");
            }

            return true;
        }

        private static void MigrateLegacyLoopIntervalAfterHydration(Recording rec)
        {
            if (rec == null
                || rec.RecordingFormatVersion >= RecordingStore.LaunchToLaunchLoopIntervalFormatVersion)
                return;

            double effectiveLoopDuration;
            double migratedLoopIntervalSeconds;
            if (!GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                    rec, rec.LoopIntervalSeconds,
                    out migratedLoopIntervalSeconds, out effectiveLoopDuration))
                return;

            double legacyLoopIntervalSeconds = rec.LoopIntervalSeconds;
            int legacyRecordingFormatVersion = rec.RecordingFormatVersion;
            rec.LoopIntervalSeconds = migratedLoopIntervalSeconds;
            NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);
            ParsekLog.Warn("Loop",
                $"RecordingStore: migrated recording '{rec.VesselName}' from legacy " +
                $"gap loopIntervalSeconds={legacyLoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                $"to launch-to-launch period={migratedLoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)}s " +
                $"using hydrated effectiveLoopDuration={effectiveLoopDuration.ToString("R", CultureInfo.InvariantCulture)}s " +
                $"for recordingFormatVersion={legacyRecordingFormatVersion} (pre-v4 loop save).");
        }

        internal static void NormalizeRecordingFormatVersionAfterLegacyLoopMigration(Recording rec)
        {
            if (rec == null
                || rec.RecordingFormatVersion >= RecordingStore.LaunchToLaunchLoopIntervalFormatVersion)
                return;

            // Legacy loop-interval migration only repairs the loop-timing semantic bump.
            // Do not silently reinterpret older RELATIVE sections as the newer v6
            // anchor-local contract just because the loop interval was normalized.
            rec.RecordingFormatVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion;
        }

        internal static bool IsAcceptableSidecarVersionLag(int probeFormatVersion, int recordingFormatVersion)
        {
            if (probeFormatVersion == recordingFormatVersion)
                return true;

            int metadataOnlyProbeVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion - 1;
            return probeFormatVersion == metadataOnlyProbeVersion
                && recordingFormatVersion == RecordingStore.LaunchToLaunchLoopIntervalFormatVersion;
        }

        /// <summary>
        /// #412: Normalize recordings whose <c>LoopIntervalSeconds</c> is below
        /// <see cref="LoopTiming.MinCycleDuration"/> while <c>LoopPlayback</c> is on.
        /// Such recordings otherwise hit <c>ResolveLoopInterval</c>'s defensive clamp on every
        /// frame. Sources include old synthetic-fixture saves (pre-#412 the RecordingBuilder
        /// persisted <c>loopIntervalSeconds=0</c>) and any hand-edited save file. Auto-repair
        /// to the effective loop duration (seamless loop at the recording's own length), falling
        /// back to <see cref="LoopTiming.DefaultLoopIntervalSeconds"/> when the
        /// trajectory can't supply a valid duration. <see cref="LoopTimeUnit.Auto"/> is left
        /// alone since the resolver pulls the value from the global slider instead.
        /// </summary>
        private static void NormalizeDegenerateLoopInterval(Recording rec)
        {
            if (rec == null || !rec.LoopPlayback) return;
            if (rec.LoopTimeUnit == LoopTimeUnit.Auto) return;
            if (rec.LoopIntervalSeconds >= LoopTiming.MinCycleDuration) return;

            double originalInterval = rec.LoopIntervalSeconds;
            double effectiveLoopDuration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            bool durationUsable = !double.IsNaN(effectiveLoopDuration)
                && !double.IsInfinity(effectiveLoopDuration)
                && effectiveLoopDuration >= LoopTiming.MinCycleDuration;
            double resolved = durationUsable
                ? effectiveLoopDuration
                : LoopTiming.DefaultLoopIntervalSeconds;

            rec.LoopIntervalSeconds = resolved;
            if (!RecordingStore.SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Warn("Loop",
                    $"NormalizeDegenerateLoopInterval: recording '{rec.VesselName}' had " +
                    $"loopIntervalSeconds={originalInterval.ToString("R", ic)} " +
                    $"(below MinCycleDuration={LoopTiming.MinCycleDuration.ToString("R", ic)}s); " +
                    $"normalizing to {resolved.ToString("R", ic)}s " +
                    $"(effectiveLoopDuration={effectiveLoopDuration.ToString("R", ic)}s, " +
                    $"durationUsable={durationUsable}) — #412 auto-repair.");
            }
        }

        internal static RecordingStore.SnapshotSidecarLoadSummary LoadSnapshotSidecarsFromPaths(
            Recording rec, string vesselPath, string ghostPath)
        {
            if (rec == null)
                throw new ArgumentNullException(nameof(rec));

            var summary = default(RecordingStore.SnapshotSidecarLoadSummary);
            rec.VesselSnapshot = null;
            rec.GhostVisualSnapshot = null;

            bool vesselFileExists = !string.IsNullOrEmpty(vesselPath) && File.Exists(vesselPath);
            bool ghostFileExists = !string.IsNullOrEmpty(ghostPath) && File.Exists(ghostPath);

            summary.VesselState = TryLoadSnapshotSidecarIfPresent(
                vesselPath, rec, "vessel", out ConfigNode vesselNode);
            summary.GhostState = RecordingStore.SnapshotSidecarLoadState.Missing;
            ConfigNode ghostNode = null;
            GhostSnapshotMode ghostSnapshotMode = rec.GhostSnapshotMode;

            if (summary.VesselState == RecordingStore.SnapshotSidecarLoadState.Loaded)
                rec.VesselSnapshot = vesselNode;

            if (ghostSnapshotMode != GhostSnapshotMode.AliasVessel)
            {
                summary.GhostState = TryLoadSnapshotSidecarIfPresent(
                    ghostPath, rec, "ghost", out ghostNode);
                if (summary.GhostState == RecordingStore.SnapshotSidecarLoadState.Loaded)
                    rec.GhostVisualSnapshot = ghostNode;
            }

            if (ghostSnapshotMode == GhostSnapshotMode.AliasVessel)
            {
                if (rec.VesselSnapshot != null)
                {
                    rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                }
                else
                {
                    summary.GhostState = TryLoadSnapshotSidecarIfPresent(
                        ghostPath, rec, "ghost", out ghostNode);
                    if (summary.GhostState == RecordingStore.SnapshotSidecarLoadState.Loaded)
                    {
                        rec.GhostVisualSnapshot = ghostNode;
                        rec.VesselSnapshot = ghostNode.CreateCopy();
                        if (!RecordingStore.SuppressLogging)
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"LoadRecordingFiles: missing vessel snapshot, recovered from ghost sidecar " +
                                $"{FormatSidecarContext(rec)} fileKind=ghost " +
                                $"vesselPath='{FormatPathForSidecarLog(vesselPath)}' " +
                                $"ghostPath='{FormatPathForSidecarLog(ghostPath)}'");
                        }
                    }
                    else if (!vesselFileExists && !ghostFileExists && !RecordingStore.SuppressLogging)
                    {
                        ParsekLog.Warn("RecordingStore",
                            $"LoadRecordingFiles: no snapshot sidecar found " +
                            $"{FormatSidecarContext(rec)} fileKind=vessel+ghost " +
                            $"vesselPath='{FormatPathForSidecarLog(vesselPath)}' " +
                            $"ghostPath='{FormatPathForSidecarLog(ghostPath)}'");
                    }
                }
            }

            // Backward compat and resilience: only a genuinely missing ghost sidecar may
            // fall back to vessel visuals. Invalid/unsupported ghost files must surface as
            // hydration failures so salvage can preserve the distinct snapshot.
            if (summary.GhostState == RecordingStore.SnapshotSidecarLoadState.Missing
                && rec.GhostVisualSnapshot == null
                && rec.VesselSnapshot != null)
            {
                rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();

                if (ghostSnapshotMode == GhostSnapshotMode.Unspecified)
                    ghostSnapshotMode = GhostSnapshotMode.AliasVessel;
                else if (ghostSnapshotMode == GhostSnapshotMode.Separate && !RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: missing ghost snapshot, fell back to vessel snapshot " +
                        $"{FormatSidecarContext(rec)} fileKind=ghost " +
                        $"vesselPath='{FormatPathForSidecarLog(vesselPath)}' " +
                        $"ghostPath='{FormatPathForSidecarLog(ghostPath)}'");
                }
            }

            if (ghostSnapshotMode == GhostSnapshotMode.Unspecified)
                ghostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);

            rec.GhostSnapshotMode = ghostSnapshotMode;
            summary.FailureReason = DetermineSnapshotLoadFailureReason(summary, rec);
            return summary;
        }

        private static RecordingStore.SnapshotSidecarLoadState TryLoadSnapshotSidecarIfPresent(
            string path, Recording rec, string label, out ConfigNode node)
        {
            node = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return RecordingStore.SnapshotSidecarLoadState.Missing;

            SnapshotSidecarProbe probe;
            bool loadOk = RecordingStore.TryLoadSnapshotSidecar(path, out node, out probe);
            if (loadOk && probe.Supported && node != null)
                return RecordingStore.SnapshotSidecarLoadState.Loaded;

            RecordingStore.SnapshotSidecarLoadState state = probe.Success && !probe.Supported
                ? RecordingStore.SnapshotSidecarLoadState.Unsupported
                : RecordingStore.SnapshotSidecarLoadState.Invalid;

            if (!RecordingStore.SuppressLogging)
            {
                string context = FormatSidecarContext(rec);
                if (state == RecordingStore.SnapshotSidecarLoadState.Unsupported)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: unsupported {label} snapshot sidecar {context} " +
                        $"fileKind={label} path='{FormatPathForSidecarLog(path)}' " +
                        SnapshotSidecarCodec.DescribeProbe(probe));
                }
                else
                {
                    ParsekLog.Warn("RecordingStore",
                        $"LoadRecordingFiles: invalid {label} snapshot sidecar {context} " +
                        $"fileKind={label} path='{FormatPathForSidecarLog(path)}' " +
                        SnapshotSidecarCodec.DescribeProbe(probe));
                }
            }

            return state;
        }

        private static string DetermineSnapshotLoadFailureReason(
            RecordingStore.SnapshotSidecarLoadSummary summary, Recording rec)
        {
            if (rec == null)
                return null;

            if (rec.VesselSnapshot == null)
            {
                if (summary.VesselState == RecordingStore.SnapshotSidecarLoadState.Invalid)
                    return "snapshot-vessel-invalid";
                if (summary.VesselState == RecordingStore.SnapshotSidecarLoadState.Unsupported)
                    return "snapshot-vessel-unsupported";
            }

            if (rec.GhostVisualSnapshot == null)
            {
                if (summary.GhostState == RecordingStore.SnapshotSidecarLoadState.Invalid)
                    return "snapshot-ghost-invalid";
                if (summary.GhostState == RecordingStore.SnapshotSidecarLoadState.Unsupported)
                    return "snapshot-ghost-unsupported";
            }

            return null;
        }

        internal static void ReconcileReadableSidecarMirrorsForKnownRecordings(
            IEnumerable<Recording> committedRecordings,
            RecordingTree pendingTree)
        {
            var seenRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            int attempted = 0;
            int failed = 0;

            ReconcileReadableSidecarMirrorsForRecordingSet(
                committedRecordings, seenRecordingIds, ref attempted, ref failed);

            if (pendingTree != null && pendingTree.Recordings != null)
            {
                ReconcileReadableSidecarMirrorsForRecordingSet(
                    pendingTree.Recordings.Values, seenRecordingIds, ref attempted, ref failed);
            }

            if (!RecordingStore.SuppressLogging && attempted > 0)
            {
                ParsekLog.Info("RecordingStore",
                    $"Readable sidecar mirror reconcile pass: attempted={attempted} failed={failed} " +
                    $"enabled={ShouldWriteReadableSidecarMirrors()}");
            }
        }

        internal static bool SaveRecordingFilesToPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath, bool incrementEpoch = true)
        {
            return SaveRecordingFilesToPathsInternal(rec, precPath, vesselPath, ghostPath, incrementEpoch);
        }

        internal static bool ReconcileReadableSidecarMirrorsToPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath)
        {
            ReadableMirrorReconcileSummary summary = ReconcileReadableSidecarMirrors(
                rec, precPath, vesselPath, ghostPath, RecordingStore.GetExpectedGhostSnapshotMode(rec));
            return !summary.Failed;
        }

        internal static bool ShouldSkipSaveToPreserveStaleSidecar(Recording rec)
        {
            if (rec == null) return false;
            if (!rec.SidecarLoadFailed) return false;

            int pointCount = rec.Points?.Count ?? 0;
            int orbitSegCount = rec.OrbitSegments?.Count ?? 0;
            int trackSectionCount = rec.TrackSections?.Count ?? 0;
            int partEventCount = rec.PartEvents?.Count ?? 0;
            int flagEventCount = rec.FlagEvents?.Count ?? 0;
            int segmentEventCount = rec.SegmentEvents?.Count ?? 0;
            bool hasVessel = rec.VesselSnapshot != null;
            bool hasGhost = rec.GhostVisualSnapshot != null;

            return pointCount == 0
                && orbitSegCount == 0
                && trackSectionCount == 0
                && partEventCount == 0
                && flagEventCount == 0
                && segmentEventCount == 0
                && !hasVessel
                && !hasGhost;
        }

        private static void ReconcileReadableSidecarMirrorsForRecordingSet(
            IEnumerable<Recording> recordings,
            HashSet<string> seenRecordingIds,
            ref int attempted,
            ref int failed)
        {
            if (recordings == null)
                return;

            foreach (var rec in recordings)
            {
                if (rec == null
                    || string.IsNullOrEmpty(rec.RecordingId)
                    || !seenRecordingIds.Add(rec.RecordingId))
                {
                    continue;
                }

                attempted++;
                if (!ReconcileReadableSidecarMirrorsForRecording(rec))
                    failed++;
            }
        }

        private static bool ReconcileReadableSidecarMirrorsForRecording(Recording rec)
        {
            if (rec == null)
                return true;

            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"Readable sidecar mirror reconcile skipped invalid recording id '{rec.RecordingId}'");
                }
                return false;
            }

            string precPath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
            string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
            string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));

            if (string.IsNullOrEmpty(precPath)
                || string.IsNullOrEmpty(vesselPath)
                || string.IsNullOrEmpty(ghostPath))
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"Readable sidecar mirror reconcile skipped unresolved path(s) for {rec.RecordingId}");
                }
                return false;
            }

            ReadableMirrorReconcileSummary summary = ReconcileReadableSidecarMirrors(
                rec, precPath, vesselPath, ghostPath, RecordingStore.GetExpectedGhostSnapshotMode(rec));
            if (summary.Failed && !RecordingStore.SuppressLogging)
            {
                ParsekLog.Warn("RecordingStore",
                    $"Readable sidecar mirror reconcile failed for {rec.RecordingId}: {summary.FailureReason}");
            }

            return !summary.Failed;
        }

        private static bool SaveRecordingFilesToPathsInternal(
            Recording rec, string precPath, string vesselPath, string ghostPath, bool incrementEpoch)
        {
            // Bug #585 follow-up: do NOT clobber a stale-sidecar .prec with empty
            // in-memory state. See ShouldSkipSaveToPreserveStaleSidecar for the
            // full rationale. The recording stays FilesDirty so future saves
            // continue to no-op (avoiding a silent stale-state-write race if the
            // hydration ever recovers later in the session). The on-disk .prec
            // and snapshots are preserved untouched, including the existing
            // SidecarEpoch — they remain authoritative until either the recorder
            // rebinds (TryRestoreActiveTreeNode salvage path clears the flag) or
            // the user invokes a destructive recording-deletion path.
            if (ShouldSkipSaveToPreserveStaleSidecar(rec))
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"SaveRecordingFiles: skipping write for {rec.RecordingId} " +
                        $"(SidecarLoadFailed=True reason='{rec.SidecarLoadFailureReason ?? "<none>"}' " +
                        $"in-memory state empty) — preserving on-disk .prec to avoid data loss " +
                        $"(bug #585 follow-up: empty-state save would clobber stale-sidecar .prec)");
                }
                // Leave FilesDirty unchanged so subsequent OnSave passes will
                // re-evaluate and skip again until the flag is cleared.
                return true;
            }

            int originalSidecarEpoch = rec.SidecarEpoch;
            GhostSnapshotMode originalGhostSnapshotMode = rec.GhostSnapshotMode;
            GhostSnapshotMode ghostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);
            rec.GhostSnapshotMode = ghostSnapshotMode;
            bool wroteVesselSnapshot = false;
            bool wroteGhostSnapshot = false;
            bool deletedStaleGhostSnapshot = false;
            var changes = new List<SidecarFileCommitBatch.StagedChange>();

            try
            {
                // Bug #270 / #290: sidecar epoch synchronization.
                // On OnSave (incrementEpoch=true): advance the epoch before writing so
                // .prec and .sfs (written later by SaveRecordingInto) stay in sync.
                // On out-of-band writes (incrementEpoch=false): preserve the current epoch
                // so the .prec matches the last OnSave's .sfs. Without this, BgRecorder
                // and scene-exit force-writes would advance the epoch independently,
                // causing false-positive staleness on quickload (bug #290).
                if (incrementEpoch)
                    rec.SidecarEpoch++;

                changes.Add(SidecarFileCommitBatch.StageWrite(path => RecordingStore.WriteTrajectorySidecar(path, rec, rec.SidecarEpoch), precPath));

                if (rec.VesselSnapshot != null)
                {
                    changes.Add(SidecarFileCommitBatch.StageWrite(path => WriteSnapshotSidecar(path, rec.VesselSnapshot), vesselPath));
                    wroteVesselSnapshot = true;
                }

                if (ghostSnapshotMode == GhostSnapshotMode.Separate && rec.GhostVisualSnapshot != null)
                {
                    if (string.IsNullOrEmpty(ghostPath))
                    {
                        if (!RecordingStore.SuppressLogging)
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"SaveRecordingFiles could not resolve ghost snapshot path " +
                                $"{FormatSidecarContext(rec, ghostSnapshotMode)} fileKind=ghost " +
                                $"trajectoryPath='{FormatPathForSidecarLog(precPath)}' " +
                                $"vesselPath='{FormatPathForSidecarLog(vesselPath)}'");
                        }
                    }
                    else
                    {
                        changes.Add(SidecarFileCommitBatch.StageWrite(path => WriteSnapshotSidecar(path, rec.GhostVisualSnapshot), ghostPath));
                        wroteGhostSnapshot = true;
                    }
                }
                else if (ghostSnapshotMode == GhostSnapshotMode.AliasVessel &&
                    !string.IsNullOrEmpty(ghostPath) &&
                    File.Exists(ghostPath))
                {
                    changes.Add(new SidecarFileCommitBatch.StagedChange
                    {
                        FinalPath = ghostPath,
                        DeleteExisting = true
                    });
                    deletedStaleGhostSnapshot = true;
                }

                SidecarFileCommitBatch.Apply(changes, () => RecordingStore.SuppressLogging);

                ReadableMirrorReconcileSummary mirrorSummary =
                    ReconcileReadableSidecarMirrors(rec, precPath, vesselPath, ghostPath, ghostSnapshotMode);
                if (mirrorSummary.Failed && !RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"SaveRecordingFiles: readable sidecar mirror reconcile failed for {rec.RecordingId}: {mirrorSummary.FailureReason}");
                }

                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"SaveRecordingFiles: id={rec.RecordingId} trajectoryEncoding={RecordingStore.GetTrajectorySidecarEncodingLabel(rec.RecordingFormatVersion)} " +
                        $"snapshotEncoding={SnapshotSidecarCodec.CurrentEncodingLabel} " +
                        $"snapshotCompression={SnapshotSidecarCodec.CurrentCompressionLevelLabel} " +
                        $"ghostSnapshotMode={ghostSnapshotMode} " +
                        $"wroteVessel={wroteVesselSnapshot} wroteGhost={wroteGhostSnapshot} " +
                        $"deletedStaleGhost={deletedStaleGhostSnapshot} " +
                        $"readableMirrorsEnabled={mirrorSummary.Enabled} " +
                        $"wroteReadableTrajectory={mirrorSummary.WroteTrajectory} " +
                        $"wroteReadableVessel={mirrorSummary.WroteVessel} " +
                        $"readableVesselSource={mirrorSummary.VesselSource ?? "None"} " +
                        $"wroteReadableGhost={mirrorSummary.WroteGhost} " +
                        $"readableGhostSource={mirrorSummary.GhostSource ?? "None"} " +
                        $"deletedReadableTrajectory={mirrorSummary.DeletedTrajectory} " +
                        $"deletedReadableVessel={mirrorSummary.DeletedVessel} " +
                        $"deletedReadableGhost={mirrorSummary.DeletedGhost}" +
                        (mirrorSummary.Failed ? " readableMirrorReconcileFailed=True" : ""));
                }

                rec.FilesDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                SidecarFileCommitBatch.CleanupStagedArtifacts(changes, () => RecordingStore.SuppressLogging);
                // Keep .sfs metadata authoritative if the sidecar write set did not
                // complete after an OnSave-triggered epoch bump.
                rec.SidecarEpoch = originalSidecarEpoch;
                rec.GhostSnapshotMode = originalGhostSnapshotMode;
                string failureContext = FormatSidecarContext(rec, rec.GhostSnapshotMode);
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Error("RecordingStore",
                        $"SaveRecordingFiles failed: {failureContext} " +
                        $"incrementEpoch={incrementEpoch} stagedFiles={changes.Count} " +
                        $"trajectoryPath='{FormatPathForSidecarLog(precPath)}' " +
                        $"vesselPath='{FormatPathForSidecarLog(vesselPath)}' " +
                        $"ghostPath='{FormatPathForSidecarLog(ghostPath)}' " +
                        $"ex={FormatExceptionForSidecarLog(ex)}");
                }
                return false;
            }
        }

        private static ReadableMirrorReconcileSummary ReconcileReadableSidecarMirrors(
            Recording rec, string precPath, string vesselPath, string ghostPath, GhostSnapshotMode ghostSnapshotMode)
        {
            var summary = new ReadableMirrorReconcileSummary
            {
                Enabled = ShouldWriteReadableSidecarMirrors()
            };
            var changes = new List<SidecarFileCommitBatch.StagedChange>();
            bool wroteTrajectory = false;
            bool wroteVessel = false;
            bool wroteGhost = false;
            bool deletedTrajectory = false;
            bool deletedVessel = false;
            bool deletedGhost = false;

            string readablePrecPath = GetReadableMirrorPath(precPath);
            string readableVesselPath = GetReadableMirrorPath(vesselPath);
            string readableGhostPath = GetReadableMirrorPath(ghostPath);

            try
            {
                if (summary.Enabled)
                {
                    changes.Add(SidecarFileCommitBatch.StageWrite(
                        path => WriteReadableTrajectoryMirror(path, rec, rec.SidecarEpoch),
                        readablePrecPath));
                    wroteTrajectory = true;

                    if (rec.VesselSnapshot != null)
                    {
                        changes.Add(SidecarFileCommitBatch.StageWrite(
                            path => WriteReadableSnapshotMirror(path, rec.VesselSnapshot),
                            readableVesselPath));
                        wroteVessel = true;
                        summary.VesselSource = "InMemory";
                    }
                    else
                    {
                        ConfigNode preservedVesselSnapshot = LoadSnapshotSidecarForReadableMirror(vesselPath);
                        if (preservedVesselSnapshot != null)
                        {
                            changes.Add(SidecarFileCommitBatch.StageWrite(
                                path => WriteReadableSnapshotMirror(path, preservedVesselSnapshot),
                                readableVesselPath));
                            wroteVessel = true;
                            summary.VesselSource = "AuthoritativeSidecar";
                        }
                    }

                    if (ghostSnapshotMode == GhostSnapshotMode.Separate && rec.GhostVisualSnapshot != null)
                    {
                        changes.Add(SidecarFileCommitBatch.StageWrite(
                            path => WriteReadableSnapshotMirror(path, rec.GhostVisualSnapshot),
                            readableGhostPath));
                        wroteGhost = true;
                        summary.GhostSource = "InMemory";
                    }
                    else if (ghostSnapshotMode == GhostSnapshotMode.AliasVessel &&
                             !string.IsNullOrEmpty(readableGhostPath) &&
                             File.Exists(readableGhostPath))
                    {
                        changes.Add(new SidecarFileCommitBatch.StagedChange
                        {
                            FinalPath = readableGhostPath,
                            DeleteExisting = true
                        });
                        deletedGhost = true;
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(readablePrecPath) && File.Exists(readablePrecPath))
                    {
                        changes.Add(new SidecarFileCommitBatch.StagedChange
                        {
                            FinalPath = readablePrecPath,
                            DeleteExisting = true
                        });
                        deletedTrajectory = true;
                    }

                    if (!string.IsNullOrEmpty(readableVesselPath) && File.Exists(readableVesselPath))
                    {
                        changes.Add(new SidecarFileCommitBatch.StagedChange
                        {
                            FinalPath = readableVesselPath,
                            DeleteExisting = true
                        });
                        deletedVessel = true;
                    }

                    if (!string.IsNullOrEmpty(readableGhostPath) && File.Exists(readableGhostPath))
                    {
                        changes.Add(new SidecarFileCommitBatch.StagedChange
                        {
                            FinalPath = readableGhostPath,
                            DeleteExisting = true
                        });
                        deletedGhost = true;
                    }
                }

                SidecarFileCommitBatch.Apply(changes, () => RecordingStore.SuppressLogging);
                summary.WroteTrajectory = wroteTrajectory;
                summary.WroteVessel = wroteVessel;
                summary.WroteGhost = wroteGhost;
                summary.DeletedTrajectory = deletedTrajectory;
                summary.DeletedVessel = deletedVessel;
                summary.DeletedGhost = deletedGhost;
            }
            catch (Exception ex)
            {
                SidecarFileCommitBatch.CleanupStagedArtifacts(changes, () => RecordingStore.SuppressLogging);
                InvalidateReadableMirrorFinalFiles(changes);
                summary.Failed = true;
                summary.FailureReason = ex.Message;
                summary.WroteTrajectory = false;
                summary.WroteVessel = false;
                summary.WroteGhost = false;
                summary.DeletedTrajectory = false;
                summary.DeletedVessel = false;
                summary.DeletedGhost = false;
                summary.VesselSource = null;
                summary.GhostSource = null;
            }

            return summary;
        }

        private static bool ShouldWriteReadableSidecarMirrors()
        {
            if (RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting.HasValue)
                return RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting.Value;

            var settings = ParsekSettings.Current;
            return settings == null || settings.writeReadableSidecarMirrors;
        }

        private static string FormatSidecarContext(Recording rec)
        {
            return FormatSidecarContext(
                rec,
                rec != null ? rec.GhostSnapshotMode : GhostSnapshotMode.Unspecified);
        }

        private static string FormatSidecarContext(Recording rec, GhostSnapshotMode ghostSnapshotMode)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "id={0} saveFolder='{1}' epoch={2} ghostSnapshotMode={3}",
                rec == null || string.IsNullOrEmpty(rec.RecordingId) ? "<null>" : rec.RecordingId,
                SafeSaveFolderForSidecarLog(),
                rec != null ? rec.SidecarEpoch : 0,
                ghostSnapshotMode);
        }

        private static string FormatPathForSidecarLog(string path)
        {
            return string.IsNullOrEmpty(path) ? "<null>" : path;
        }

        private static string FormatExceptionForSidecarLog(Exception ex)
        {
            if (ex == null)
                return "<none>";

            return ex.GetType().Name + ":" + (ex.Message ?? string.Empty);
        }

        private static string SafeSaveFolderForSidecarLog()
        {
            try
            {
                return string.IsNullOrEmpty(HighLogic.SaveFolder) ? "<null>" : HighLogic.SaveFolder;
            }
            catch (Exception ex)
            {
                return "<error:" + ex.GetType().Name + ">";
            }
        }

        private static void WriteReadableTrajectoryMirror(string path, Recording rec, int sidecarEpoch)
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", rec?.RecordingFormatVersion ?? 0);
            if (rec != null && !string.IsNullOrEmpty(rec.RecordingId))
                precNode.AddValue("recordingId", rec.RecordingId);
            precNode.AddValue("sidecarEpoch", sidecarEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RecordingStore.SerializeTrajectoryInto(precNode, rec);
            SafeWriteConfigNode(precNode, path);
        }

        // Thin duplicate of RecordingStore's snapshot writer until codec ownership settles.
        private static void WriteSnapshotSidecar(string path, ConfigNode node)
        {
            SnapshotSidecarCodec.Write(path, node);
        }

        private static void WriteReadableSnapshotMirror(string path, ConfigNode node)
        {
            SafeWriteConfigNode(node, path);
        }

        private static ConfigNode LoadSnapshotSidecarForReadableMirror(string authoritativePath)
        {
            if (string.IsNullOrEmpty(authoritativePath) || !File.Exists(authoritativePath))
                return null;

            if (!RecordingStore.TryLoadSnapshotSidecar(authoritativePath, out ConfigNode node, out SnapshotSidecarProbe probe))
            {
                throw new InvalidOperationException(
                    $"failed to load authoritative snapshot sidecar '{Path.GetFileName(authoritativePath)}' for readable mirror");
            }

            if (!probe.Supported)
            {
                throw new InvalidOperationException(
                    $"unsupported authoritative snapshot sidecar '{Path.GetFileName(authoritativePath)}' for readable mirror");
            }

            return node;
        }

        private static void InvalidateReadableMirrorFinalFiles(IEnumerable<SidecarFileCommitBatch.StagedChange> changes)
        {
            if (changes == null)
                return;

            foreach (var change in changes)
            {
                string finalPath = change != null ? change.FinalPath : null;
                if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
                    continue;

                try
                {
                    File.Delete(finalPath);
                }
                catch (Exception ex)
                {
                    if (!RecordingStore.SuppressLogging)
                    {
                        ParsekLog.Warn("RecordingStore",
                            $"Readable sidecar mirror invalidate failed for {Path.GetFileName(finalPath)}: {ex.Message}");
                    }
                }
            }
        }

        private static string GetReadableMirrorPath(string authoritativePath)
        {
            return string.IsNullOrEmpty(authoritativePath) ? null : authoritativePath + ".txt";
        }

        // Thin duplicate of RecordingStore's config-node writer until codec ownership settles.
        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "RecordingStore");
        }

        private struct ReadableMirrorReconcileSummary
        {
            public bool Enabled;
            public bool Failed;
            public bool WroteTrajectory;
            public bool WroteVessel;
            public bool WroteGhost;
            public bool DeletedTrajectory;
            public bool DeletedVessel;
            public bool DeletedGhost;
            public string VesselSource;
            public string GhostSource;
            public string FailureReason;
        }
    }
}
