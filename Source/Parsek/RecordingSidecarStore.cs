using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    internal static class RecordingSidecarStore
    {
        // Kept local during the wrapper-facade split so legacy log text stays stable.
        // Consolidate with RecordingStore.Log after sidecar/codec ownership settles.
        private const string LegacyPrefix = "[Parsek] ";

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

        // Mirrors RecordingStore.Log while preserving legacy prefix and WARN normalization.
        private static void Log(string message)
        {
            if (RecordingStore.SuppressLogging) return;

            string clean = message ?? "(empty)";
            if (clean.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(LegacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("RecordingStore", trimmed);
                return;
            }

            ParsekLog.Info("RecordingStore", clean);
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
