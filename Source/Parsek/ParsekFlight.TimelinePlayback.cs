using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Timeline auto-playback driven through the engine (chain ghosts, skip reasons, playback
    // flags, CommNet, loop units, anchor resolvers, timeline ghost teardown), manual preview
    // playback, and the camera pre-cull ghost-position reapply.
    public partial class ParsekFlight
    {
        internal enum GhostCameraPreCullAction
        {
            /// <summary>No pending entries, or this frame's post-LateUpdate pass already ran.</summary>
            Nothing,

            /// <summary>
            /// A camera is rendering before this frame's LateUpdate ran (a manual
            /// Camera.Render() from Update or a coroutine). LateUpdate has not yet
            /// reapplied or terrain-clamped the entries, so they are left for it.
            /// </summary>
            KeepForLateUpdate,

            /// <summary>First camera after this frame's LateUpdate: run the pass and consume the entries.</summary>
            RunAndConsume,
        }

        /// <summary>
        /// Decides what the Camera.onPreCull hook does with the frame's ghost reapply
        /// entries. Every render after LateUpdate sees the same final pose only if the
        /// entries reach LateUpdate intact: a pre-cull that fires earlier in the frame
        /// (stock kerbal portraits, any mod camera rendered from a coroutine) must not
        /// consume them, or that frame renders surface ghosts at the raw Update pose
        /// without the terrain clamp while the neighbouring frames render them clamped.
        /// </summary>
        internal static GhostCameraPreCullAction ResolveGhostCameraPreCullAction(
            int frameCount,
            int lastLateUpdateReapplyFrame,
            int lastPreCullReapplyFrame,
            int pendingEntryCount)
        {
            if (pendingEntryCount <= 0)
                return GhostCameraPreCullAction.Nothing;
            if (lastLateUpdateReapplyFrame != frameCount)
                return GhostCameraPreCullAction.KeepForLateUpdate;
            if (lastPreCullReapplyFrame == frameCount)
                return GhostCameraPreCullAction.Nothing;
            return GhostCameraPreCullAction.RunAndConsume;
        }

        internal static bool ShouldProcessGhostPositionReapply(GhostPositionReapplyPhase phase)
        {
            return phase != GhostPositionReapplyPhase.CameraPreCull;
        }

        /// <summary>
        /// Returns the GhostChain if this recording is a chain tip that should spawn via VesselGhoster.
        /// Returns null for non-chain recordings, intermediate links, or terminated chains.
        /// </summary>
        internal static GhostChain FindChainTipForRecording(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return null;

            foreach (var kvp in chains)
            {
                if (kvp.Value.TipRecordingId == rec.RecordingId && !kvp.Value.IsTerminated)
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Finds the first committed recording that covers the given UT for a vessel PID.
        /// This is a global PID-only lookup. Chain playback must use the chain-scoped
        /// helper below to avoid selecting a different alternate-history tree that
        /// happens to reuse the same PID.
        /// </summary>
        internal static Recording FindBackgroundRecordingForVessel(
            IReadOnlyList<Recording> committedRecordings,
            uint vesselPid,
            double currentUT,
            ISet<string> excludedTreeIds = null)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (excludedTreeIds != null
                    && !string.IsNullOrEmpty(rec.TreeId)
                    && excludedTreeIds.Contains(rec.TreeId))
                    continue;
                if (rec.VesselPersistentId == vesselPid &&
                    rec.Points != null && rec.Points.Count > 0 &&
                    currentUT >= rec.StartUT && currentUT <= rec.EndUT)
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Finds a point-backed trajectory recording for a chain at the given UT.
        /// Uses the exact chain path within committed trees when available, and only
        /// falls back to global PID lookup before the first claim.
        /// </summary>
        internal static Recording FindBackgroundRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (committedRecordings == null || chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (chainRec != null)
                return chainRec.Points != null && chainRec.Points.Count > 0 ? chainRec : null;

            if (ParsekFlightPlaybackHost.ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return preClaimRec != null && preClaimRec.Points != null && preClaimRec.Points.Count > 0
                    ? preClaimRec : null;

            return FindBackgroundRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain) : null);
        }

        internal static Recording FindPreClaimChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT,
            out bool hasChainLocalCoverage,
            out bool isAmbiguous)
        {
            hasChainLocalCoverage = false;
            isAmbiguous = false;
            if (committedTrees == null || chain == null || chain.Links == null || chain.Links.Count == 0)
                return null;

            var firstClaimTreeIds = ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain);
            if (firstClaimTreeIds.Count == 0)
                return null;

            Recording unique = null;
            foreach (var treeId in firstClaimTreeIds)
            {
                RecordingTree firstClaimTree = FindCommittedTree(committedTrees, treeId);
                if (firstClaimTree == null)
                    continue;

                foreach (var rec in firstClaimTree.Recordings.Values)
                {
                    if (rec.VesselPersistentId != chain.OriginalVesselPid)
                        continue;
                    if (currentUT < rec.StartUT || currentUT > rec.EndUT)
                        continue;
                    if (!ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(rec, currentUT))
                        continue;

                    if (unique != null)
                    {
                        isAmbiguous = true;
                        hasChainLocalCoverage = true;
                        return null;
                    }

                    unique = rec;
                }
            }

            hasChainLocalCoverage = unique != null;
            return unique;
        }

        internal static Recording FindExactChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees, GhostChain chain, double currentUT)
        {
            if (committedTrees == null || chain == null || chain.Links == null)
                return null;

            for (int i = chain.Links.Count - 1; i >= 0; i--)
            {
                var link = chain.Links[i];
                if (link.ut > currentUT + 0.001)
                    continue;

                return ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(
                    committedTrees, link, chain.OriginalVesselPid, currentUT);
            }

            return null;
        }

        /// <summary>
        /// Finds the best non-point playback source for positioning a chain ghost when
        /// point-backed trajectory playback is unavailable.
        /// </summary>
        internal static Recording FindPositionFallbackRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(chainRec, currentUT))
                return chainRec;

            if (ParsekFlightPlaybackHost.ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(preClaimRec, currentUT)
                    ? preClaimRec
                    : null;

            return ParsekFlightPlaybackHost.FindPlaybackFallbackRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain) : null);
        }

        internal static RecordingTree FindCommittedTree(
            IReadOnlyList<RecordingTree> committedTrees, string treeId)
        {
            if (committedTrees == null || string.IsNullOrEmpty(treeId))
                return null;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (committedTrees[i].Id == treeId)
                    return committedTrees[i];
            }

            return null;
        }

        /// <summary>
        /// Stamps the chain's last-rendered map orbit from <paramref name="seg"/> and reports
        /// whether it changed. Change detection reads body, SMA, and eccentricity (covers SOI
        /// transitions, orbit changes, and inclination maneuvers at constant altitude). Exact
        /// equality is intentional: segment values are stored doubles that don't drift, so a
        /// difference means a different OrbitSegment was found, not floating-point accumulation.
        /// An unchanged segment leaves the chain untouched and returns false, which is the
        /// caller's early-out; a changed one writes the three fields and returns true.
        /// </summary>
        internal static bool TryStampChainMapOrbit(GhostChain chain, OrbitSegment seg)
        {
            if (chain == null) return false;

            if (seg.bodyName == chain.LastMapOrbitBodyName
                && seg.semiMajorAxis == chain.LastMapOrbitSma
                && seg.eccentricity == chain.LastMapOrbitEcc)
                return false;

            chain.LastMapOrbitBodyName = seg.bodyName;
            chain.LastMapOrbitSma = seg.semiMajorAxis;
            chain.LastMapOrbitEcc = seg.eccentricity;
            return true;
        }

        /// <summary>
        /// True when <paramref name="rec"/> is the tip of an active ghost chain in this scene, so
        /// its end-of-recording spawn goes through the chain path (VesselGhoster), which owns the
        /// chain cleanup. The policy's warp-time KSC retirement shortcut skips such a tip.
        /// </summary>
        internal bool IsActiveGhostChainTipFromPolicy(Recording rec)
        {
            return rec != null && FindChainTipForRecording(activeGhostChains, rec) != null;
        }

        /// <summary>Called by policy when a mid-chain segment ends while watched.</summary>
        internal int FindNextWatchTargetFromPolicy(int currentIndex, Recording currentRec) =>
            watchMode.FindNextWatchTarget(currentIndex, currentRec);

        /// <summary>Called by policy to transfer watch to the next chain segment.</summary>
        internal bool TransferWatchToNextSegmentFromPolicy(int nextIndex) =>
            watchMode.TransferWatchToNextSegment(nextIndex);

        /// <summary>Called by policy to exit watch mode.</summary>
        internal void ExitWatchModeFromPolicy() => watchMode.ExitWatchModePreservingLineage();

        internal void ExitWatchModePreservingLineage(bool skipCameraRestore = false) =>
            watchMode.ExitWatchModePreservingLineage(skipCameraRestore);

        /// <summary>Called by policy to start the watch-mode hold timer at recording end.</summary>
        internal void StartWatchHoldFromPolicy(
            float holdUntilRealTime,
            double pendingActivationUT = double.NaN,
            float holdMaxRealTime = -1f) =>
            watchMode.StartWatchHold(holdUntilRealTime, pendingActivationUT, holdMaxRealTime);

        private static float GetCurrentWarpRateSafe()
        {
            try
            {
                return TimeWarp.CurrentRate;
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host does not provide the live KSP warp singleton.
                return 1f;
            }
            catch (MethodAccessException)
            {
                return 1f;
            }
            catch (MissingMethodException)
            {
                return 1f;
            }
        }

        internal static GhostPlaybackSkipReason ResolveGhostPlaybackSkipReason(
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false,
            bool rewindRetired = false,
            bool historicalNeverReplayed = false)
        {
            if (!hasRenderableData)
                return GhostPlaybackSkipReason.NoRenderableData;
            if (!playbackEnabled)
                return GhostPlaybackSkipReason.PlaybackDisabled;
            if (rewindRetired)
                return GhostPlaybackSkipReason.RewindRetired;
            if (supersededByRelation)
                return GhostPlaybackSkipReason.SupersededByRelation;
            if (externalVesselSuppressed)
                return GhostPlaybackSkipReason.ExternalVesselSuppressed;
            // BUG-B: keep a purely-historical committed recording (never rewound to
            // replay) dormant. Ordered last so a structural/career skip still wins;
            // only an otherwise-renderable recording is parked as historical.
            if (historicalNeverReplayed)
                return GhostPlaybackSkipReason.HistoricalNotReplayed;
            return GhostPlaybackSkipReason.None;
        }

        internal static void LogGhostSkipReasonChangeForTesting(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false)
        {
            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeCore(
                index,
                recordingId,
                vesselName,
                reason,
                hasRenderableData,
                playbackEnabled,
                externalVesselSuppressed,
                supersededByRelation);
        }

        internal struct FastForwardWatchHandoffDecision
        {
            public bool canEnterWatch;
            public bool warn;
            public int index;
            public bool recordingExists;
            public bool playbackEnabled;
            public bool activeGhost;
            public GhostPlaybackSkipReason skipReason;
            public string reason;
            public string recordingId;
            public string vesselName;
        }

        internal static FastForwardWatchHandoffDecision ClassifyFastForwardWatchHandoff(
            string pendingRecordingId,
            IReadOnlyList<Recording> committed,
            TrajectoryPlaybackFlags[] flags,
            Func<int, bool> hasActiveGhost)
        {
            var stale = new FastForwardWatchHandoffDecision
            {
                canEnterWatch = false,
                warn = false,
                index = -1,
                recordingExists = false,
                playbackEnabled = false,
                activeGhost = false,
                skipReason = GhostPlaybackSkipReason.None,
                reason = "stale-recording-id",
                recordingId = pendingRecordingId,
                vesselName = "?"
            };

            if (string.IsNullOrEmpty(pendingRecordingId) || committed == null)
                return stale;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || rec.RecordingId != pendingRecordingId)
                    continue;

                bool activeGhost = hasActiveGhost != null && hasActiveGhost(i);
                GhostPlaybackSkipReason skipReason = flags != null && i < flags.Length
                    ? flags[i].skipReason
                    : ResolveGhostPlaybackSkipReason(
                        GhostPlaybackEngine.HasRenderableGhostData(rec),
                        rec.GhostVisible,
                        externalVesselSuppressed: false);
                string reason = activeGhost
                    ? "active-ghost"
                    : (skipReason != GhostPlaybackSkipReason.None
                        ? skipReason.ToLogToken()
                        : "active-ghost-missing");
                return new FastForwardWatchHandoffDecision
                {
                    canEnterWatch = activeGhost,
                    warn = !activeGhost,
                    index = i,
                    recordingExists = true,
                    playbackEnabled = rec.GhostVisible,
                    activeGhost = activeGhost,
                    skipReason = skipReason,
                    reason = reason,
                    recordingId = rec.RecordingId,
                    vesselName = rec.VesselName
                };
            }

            return stale;
        }

        internal static string BuildFastForwardWatchHandoffMessage(
            FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Deferred FF watch handoff {0}: id={1} index={2} vessel=\"{3}\" " +
                "committedExists={4} playbackEnabled={5} currentUT={6:F1} activeGhost={7} " +
                "skipReason={8} reason={9}",
                decision.canEnterWatch ? "ready" : "failed",
                string.IsNullOrEmpty(decision.recordingId) ? "(none)" : decision.recordingId,
                decision.index,
                decision.vesselName ?? "?",
                decision.recordingExists,
                decision.playbackEnabled,
                currentUT,
                decision.activeGhost,
                decision.skipReason.ToLogToken(),
                decision.reason ?? "(none)");
        }

        internal static bool ResolveReFlySettleStabilityForTesting(
            Recording rec,
            int frame,
            out string anchorRecordingId,
            out string reason)
        {
            return ParsekFlightPlaybackHost.ResolveReFlySettleStability(rec, frame, out anchorRecordingId, out reason);
        }

        /// <summary>
        /// Pure-static predicate for <c>TrajectoryPlaybackFlags.isChainSeamSuccessor</c>: the
        /// successor qualifies for chain-seam handoff treatment iff its branch-0 predecessor in
        /// the same chain currently has a ghost state and the current UT is at or past the
        /// successor's own seam UT — i.e. its first playable payload sample (chain handoff
        /// moment).
        /// <para>
        /// The seam UT is the SUCCESSOR's activation-start UT, NOT the predecessor's
        /// <c>Recording.EndUT</c>. The predecessor's <c>EndUT</c> is the recording's outer
        /// semantic envelope and is widened by <c>ExplicitEndUT</c> (orbit-tail projections,
        /// Re-Fly tail estimates), so on a chain-head with an orbit projection the predecessor
        /// <c>EndUT</c> can sit far past the actual chain handoff (the Kerbal X playtest
        /// 2026-05-19 had predecessor <c>EndUT</c>=1289 while the seam was at UT 123.54). The
        /// successor's activation UT is the true seam contract because that is when the engine
        /// first-spawns the successor.
        /// </para>
        /// <para>
        /// The <c>1e-6</c> epsilon mirrors other "at or past" comparisons in the engine
        /// (e.g. early debris completion at <c>GhostPlaybackEngine.cs</c>) so floating-point
        /// dust around an exact endpoint match doesn't flip the predicate frame-to-frame.
        /// </para>
        /// </summary>
        internal static bool IsChainSeamSuccessor(
            double currentUT, double successorSeamUT, bool predecessorHasGhostState)
        {
            if (!predecessorHasGhostState) return false;
            return currentUT + 1e-6 >= successorSeamUT;
        }

        /// <summary>
        /// Builds the batched spawn-suppression summary line: a total plus a
        /// per-reason histogram sorted by descending count (ties broken by reason text
        /// for determinism). Replaces the former one-line-per-recording diagnostic that
        /// was the worst log-spam source. Pure for unit testing.
        /// </summary>
        internal static string BuildSpawnSuppressedSummary(
            int total, IReadOnlyDictionary<string, int> byReason)
        {
            string head = "Spawn suppressed: "
                + total.ToString(CultureInfo.InvariantCulture) + " recording(s)";
            if (byReason == null || byReason.Count == 0)
                return head;

            var ordered = new List<KeyValuePair<string, int>>(byReason);
            ordered.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            var parts = new List<string>(ordered.Count);
            for (int r = 0; r < ordered.Count; r++)
                parts.Add(ordered[r].Key + "=" + ordered[r].Value.ToString(CultureInfo.InvariantCulture));
            return head + " | " + string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Host-side row gate for render-only session-suppression carve-outs.
        /// Route-specific narrowing stays inside the playback engine.
        /// </summary>
        internal static bool RecordingEligibleForSessionSuppressedRenderCarveOut(Recording rec)
        {
            return rec != null
                && rec.MergeState != MergeState.NotCommitted
                && !string.IsNullOrWhiteSpace(rec.RecordingId);
        }

        /// <summary>The flight scene's ghost CommNet manager (in-game tests); null before the first tick.</summary>
        internal GhostCommNetManager GhostCommNet => ghostCommNet;

        internal static bool ShouldRecordGhostAnchorCandidates(ReFlySessionMarker marker)
        {
            return marker != null && !string.IsNullOrWhiteSpace(marker.ActiveReFlyRecordingId);
        }

        internal static RelativeAnchorResolverContext BuildFlightRelativeAnchorResolverContext(
            RecordingTree focusTree,
            string focusedRecordingId,
            ReFlySessionMarker marker)
        {
            return new RelativeAnchorResolverContext(
                focusTree,
                focusRecordingId: focusedRecordingId,
                focusTreeId: focusTree?.Id,
                activeReFlyMarker: marker,
                pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null,
                absoluteWorldPositionResolver: ParsekFlightPlaybackHost.ResolveAnchorResolverPointWorldPosition,
                bodyWorldRotationResolver: ParsekFlightPlaybackHost.ResolveAnchorResolverBodyWorldRotation,
                orbitalCheckpointPoseResolver: ParsekFlightPlaybackHost.TryResolveFlightOrbitalAnchorPose,
                tryResolveLiveAnchorTransform: TryGetLiveAnchorTransformDelegate(),
                tryResolveLiveLaunchMatchedAnchorPose: TryResolveLiveLaunchMatchedAnchorPoseForResolver);
        }

        // Logistics route live-anchor bind (Step 1): resolves the LIVE pose of an
        // anchor recording's own launch-matched live vessel for the relative
        // resolver. Returns null (recorded-anchor fallback) when: the recording is
        // null / has no pid; the launch-matched live vessel is not loaded (guid-gated
        // RealVesselExistsForRecording, so a same-craft DIFFERENT-launch live vessel
        // never binds); or the live transform is unavailable. Reuses
        // TryResolveLoopLiveAnchorPose so the anchor frame is the recorder's
        // vesselTransform.position / rotation (NOT GetWorldPos3D/CoMD), keeping the
        // docking attitude correct. Headless-safe: RealVesselExistsForRecording and
        // FindVesselByPid both return false/null without FlightGlobals.
        //
        // The bind is guid-GATED (RealVesselExistsForRecording) but the transform is
        // then resolved by pid (FindVesselByPid inside TryResolveLoopLiveAnchorPose).
        // KSP regenerates a craft-baked pid on collision with a currently-live vessel,
        // so two same-craft launches cannot be simultaneously loaded sharing the pid;
        // the pid-only transform resolve therefore cannot pick the wrong live vessel
        // after the guid gate passes.
        //
        // UT-ALIGNMENT: `ut` is intentionally unused here — the bind returns the live
        // anchor's CURRENT transform, not its pose at `ut`. Correct for the steady
        // route-station case; for the broadening risk on non-looped relative ghosts
        // whose live anchor has moved on, see the "Known limitation / playtest-verify"
        // note in docs/dev/todo-and-known-bugs.md (2026-06-13 entry).
        internal static (Vector3d pos, Quaternion rot)? TryResolveLiveLaunchMatchedAnchorPoseForResolver(
            Recording anchorRec,
            double ut)
        {
            if (anchorRec == null || anchorRec.VesselPersistentId == 0u)
                return null;

            ParsekFlight instance = Instance;
            if (instance == null)
                return null;

            if (!GhostPlaybackLogic.RealVesselExistsForRecording(anchorRec))
                return null;

            if (!instance.Positioner.TryResolveLoopLiveAnchorPose(
                    anchorRec.VesselPersistentId,
                    anchorRec.RecordingId,
                    ut,
                    out RelativeAnchorPose pose))
            {
                return null;
            }

            // Step 1 (the live-bind pose) is all that happens here. The resolver stamps
            // the live-bind ledger (RelativeAnchorResolver.RecordLiveBoundAnchor) on the
            // source=live bind event, which the Step-2 double-suppression consumes one
            // frame later via RelativeAnchorResolver.WasLiveBoundThisOrLastFrame: the
            // anchor's own loop ghost is hidden ONLY while a relative member is actually
            // docking its live vessel (scoped to the live-bind event), NOT for the whole
            // loop merely because its guid-matched live vessel is loaded.
            return (pose.worldPos, pose.worldRotation);
        }

        internal static Func<uint, string, double, (Vector3d pos, Quaternion rot)?> TryGetLiveAnchorTransformDelegate()
        {
            return LiveAnchorTransformDelegate;
        }

        private static (Vector3d pos, Quaternion rot)? TryResolveLiveAnchorTransformForResolver(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT)
        {
            ParsekFlight instance = Instance;
            if (instance == null)
                return null;

            if (!instance.Positioner.TryResolveLoopLiveAnchorPose(
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    out RelativeAnchorPose pose))
            {
                return null;
            }

            return (pose.worldPos, pose.worldRotation);
        }


        // UpdateLoopingTimelinePlayback removed (T25 Phase 9)

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        // UpdateOverlapLoopPlayback removed (T25 Phase 9)

        // Phase F: ApplyResourceDeltas, ApplyTreeResourceDeltas, ApplyTreeLumpSum
        // removed. The ledger (GameActions/RecalculationEngine) is the single source
        // of truth for funds/science/reputation; per-recording lump-sum replay and
        // tree-level snapshot reconciliation are no longer used.

        internal RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId) return trees[i];
            }
            Log($"FindCommittedTree: treeId='{treeId}' not found (checked {trees.Count} committed trees)");
            return null;
        }
    }
}
