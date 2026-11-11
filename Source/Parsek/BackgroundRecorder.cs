using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Carries parent vessel engine/RCS state to child recordings at decouple time.
    /// Consumed once by InitializeLoadedState to seed engines that SeedEngines missed
    /// (isOperational=false after fuel severance). Bug #298.
    /// </summary>
    internal struct InheritedEngineState
    {
        public HashSet<ulong> activeEngineKeys;
        public Dictionary<ulong, float> engineThrottles;
        public HashSet<ulong> activeRcsKeys;
        public Dictionary<ulong, float> rcsThrottles;

        /// <summary>
        /// Creates a snapshot from a FlightRecorder's active engine/RCS state.
        /// Returns null if no engines or RCS are active. Defensively copies all
        /// collections (the recorder's fields are live mutable references).
        ///
        /// <para>
        /// The departed-part carry-over is unioned in. This snapshot is taken from
        /// <c>ParsekFlight.ProcessBreakupEvent</c>, which runs a whole crash-coalescer window AFTER
        /// the split — by then <c>FlightRecorder.OnVesselWasModified</c> has already pruned the
        /// separated stack's keys out of the ACTIVE sets (so they cannot rot into a stale terminal
        /// EngineShutdown on the parent). Without the union, the child debris recording would lose
        /// the one fact #298 exists to carry: the booster was at full throttle when it came off.
        /// <c>MergeInheritedEngineState</c> filters the result by the child's own part pids, so a
        /// carry-over key can only ever reach the child that actually holds that part.
        /// </para>
        /// </summary>
        internal static InheritedEngineState? FromRecorder(FlightRecorder rec)
        {
            if (rec == null) return null;

            HashSet<ulong> engineKeys, rcsKeys;
            Dictionary<ulong, float> engineThrottles, rcsThrottles;
            FlightRecorder.UnionDepartedIntoInheritedState(
                rec.ActiveEngineKeys, rec.LastEngineThrottles, rec.DepartedEngineThrottles,
                out engineKeys, out engineThrottles);
            FlightRecorder.UnionDepartedIntoInheritedState(
                rec.ActiveRcsKeys, rec.LastRcsThrottles, rec.DepartedRcsThrottles,
                out rcsKeys, out rcsThrottles);

            if (engineKeys == null && rcsKeys == null) return null;

            return new InheritedEngineState
            {
                activeEngineKeys = engineKeys,
                engineThrottles = engineThrottles,
                activeRcsKeys = rcsKeys,
                rcsThrottles = rcsThrottles
            };
        }

    }

    /// <summary>
    /// Manages continuous recording for background vessels in a recording tree.
    /// Supports two modes per vessel:
    ///   - On-rails: captures OrbitSegment or SurfacePosition snapshots
    ///   - Loaded/physics: full trajectory points, part events, adaptive sampling
    ///
    /// Not a MonoBehaviour. Owned by ParsekFlight, which calls its methods from
    /// Update() and PhysicsFramePatch.Postfix.
    /// </summary>
    internal partial class BackgroundRecorder
    {
        private enum BackgroundRecordingEndReason
        {
            Unknown = 0,
            VesselDestroyedOrDespawned = 1,
            TtlExpired = 2,
            LeftPhysicsBubble = 3,
            ParentRecordingMissing = 4,
            ParentRecordingClosedOrSuperseded = 5,
            ParentOnRailsOrDestroyed = 6
        }

        private RecordingTree tree;

        // Per-vessel tracking state for loaded/physics mode
        private Dictionary<uint, BackgroundVesselState> loadedStates
            = new Dictionary<uint, BackgroundVesselState>();

        // Per-vessel on-rails tracking
        private Dictionary<uint, BackgroundOnRailsState> onRailsStates
            = new Dictionary<uint, BackgroundOnRailsState>();

        /// <summary>
        /// M6: the part-tracking state a BG vessel carried into its rails span, snapshotted (as a
        /// DEEP copy — the originals die with <c>loadedStates</c>) at the loaded→on-rails
        /// transition, after the terminal engine/RCS/robotic events were emitted. Consumed once by
        /// the matching off-rails re-entry, which diffs the freshly seeded live state against it so
        /// a change that happened across the warp is RECORDED rather than silently re-synced.
        /// Bounded by the number of BG members; dropped on consume and at every BG teardown site.
        ///
        /// <para>
        /// Deliberately NOT serialized. A save/load or scene change tears this recorder down and
        /// rebuilds it, so a span that straddles one finds no snapshot and degrades to the old
        /// skip-the-seed behaviour — a missed reconcile, never a wrong one. Persisting it would put
        /// a second copy of every tracking set in the `.sfs` to buy back one edge case.
        /// </para>
        /// </summary>
        private Dictionary<uint, PartTrackingSets> railsSpanPartStates
            = new Dictionary<uint, PartTrackingSets>();

        /// <summary>
        /// True when this recorder is tracking any LOADED (physics-bubble)
        /// background member. Loaded members accrue per-frame trajectory + part
        /// events that <see cref="CheckpointAllVessels"/> does NOT flush into the
        /// tree recordings (it only closes on-rails orbit segments), so they
        /// cannot be cleanly tail-checked. The no-session committed-resume no-op
        /// revert DEFERS when this is true (a loaded member may have recorded real
        /// trajectory during the resume). On-rails members, by contrast, ARE
        /// flushable via CheckpointAllVessels and are evaluated per-recording.
        /// </summary>
        internal bool HasLoadedBackgroundMembers => loadedStates.Count > 0;

        // Per-vessel last-known finalization tails. Consumers land in later phases;
        // this phase only owns refresh and transfer.
        private Dictionary<uint, RecordingFinalizationCache> finalizationCaches
            = new Dictionary<uint, RecordingFinalizationCache>();

        // GameEvent subscriptions for discrete events (onPartDie, onPartJointBreak)
        private bool partEventsSubscribed;

        // Interval for updating ExplicitEndUT on background recordings
        private const double ExplicitEndUpdateInterval = 30.0;
        private const double BackgroundStateDriftCheckInterval = 5.0;
        private const double BranchBoundaryUTTolerance = 1e-6;
        private double lastBackgroundStateDriftCheckUT = double.MinValue;
        // FixedUpdate-step physics callbacks that took no trajectory sample (diagnostics only).
        private int fixedStepSampleSkipCount;

        // Debris TTL: stop recording debris after this many seconds
        internal const double DebrisTTLSeconds = 60.0;
        internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
        internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;
        internal const double DebrisRelativeSectionExitMeters = 550.0;

        // Cascade depth cap. Background vessel splits whose parent recording is at
        // Generation >= MaxRecordingGeneration are skipped entirely — the parent's
        // existing recording continues sampling, the new fragments are not tracked.
        // Currently 1 = "only record primary debris (decoupled boosters)". See #284.
        internal const int MaxRecordingGeneration = 1;

        // Per-vessel debris TTL tracking (vesselPid -> UT at which to stop recording)
        // double.NaN = no TTL (controlled vessel, keep recording indefinitely)
        private Dictionary<uint, double> debrisTTLExpiry
            = new Dictionary<uint, double>();

        // Pending background split checks: after OnBackgroundPartJointBreak,
        // we defer one frame to let KSP finalize the vessel split, then check.
        // The parentBoundaryPoint is captured at the actual joint-break UT so
        // parent continuation/closure can use an exact split-time pose instead
        // of backdating a later sample from the deferred check frame.
        // splitCause is the BranchPoint.SplitCause the deferred check stamps
        // ("DECOUPLE"; "UNDOCK" for a docking-port undock; "BROKE_OFF" for a joint that
        // broke under force).
        private Dictionary<uint, (double branchUT, string recordingId, TrajectoryPoint? parentBoundaryPoint, string splitCause)>
            pendingBackgroundSplitChecks
                = new Dictionary<uint, (double, string, TrajectoryPoint?, string)>();

        // Docking-port undock handoff for background vessels. KSP's Part.Undock fires
        // onPartUndock(part) first, then destroys part.attachJoint (the synchronous
        // onPartJointBreak whose Child is that same part), all inside one call at one UT.
        // The undock handler notes the part here and the joint-break handler consumes it.
        // A pre-attached (VAB-built) port's Undock goes through Part.decouple instead, which
        // fires onPartDeCouple(part) before destroying the same attachJoint; the decouple
        // handler notes that part in the same slot.
        private uint pendingBackgroundUndockPartPid;
        private double pendingBackgroundUndockUT = double.NaN;

        // Pre-break snapshot of all vessel PIDs in FlightGlobals, per parent vessel.
        // Used to identify NEW vessels that appeared from the split.
        private Dictionary<uint, HashSet<uint>> preBreakVesselPidSnapshots
            = new Dictionary<uint, HashSet<uint>>();

        // Consumed on the first loaded-state init after a branch backgrounds a vessel.
        // This survives the "backgrounded while not yet loaded" path used by EVA splits.
        private Dictionary<uint, SegmentEnvironment> pendingInitialEnvironmentOverrides
            = new Dictionary<uint, SegmentEnvironment>();

        // Optional branch-boundary point captured when a split child is first created.
        // Consumed when the vessel opens its first loaded background TrackSection so the
        // recording has a real playable payload at the split moment instead of waiting for
        // the first later proximity sample.
        private Dictionary<uint, TrajectoryPoint> pendingInitialTrajectoryPoints
            = new Dictionary<uint, TrajectoryPoint>();

        // Focused-vessel breakup can create debris before the active parent's open
        // TrackSection has been flushed into tree.Recordings. Queue the parent
        // structural-event point so the debris seed can still use the split-UT
        // parent pose instead of the live parent pose at background initialization.
        private Dictionary<uint, (string parentRecordingId, TrajectoryPoint point)> pendingDebrisSeedParentAnchorPoints
            = new Dictionary<uint, (string, TrajectoryPoint)>();

        // Reusable buffer for transition-check methods (avoids per-frame List<PartEvent> allocations)
        private readonly List<PartEvent> reusableEventBuffer = new List<PartEvent>();

        // Robotic sampling constants (mirrors FlightRecorder)
        private const double roboticSampleIntervalSeconds = 0.25;
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;
        private const float backgroundAttitudeSampleThresholdDegrees = 1.0f;

        // Injectable vessel finder for CheckpointAllVessels (null = use FlightRecorder.FindVesselByPid)
        private System.Func<uint, Vessel> vesselFinderOverride;

        // Injectable distance override for testing (null = use FlightGlobals.ActiveVessel)
        private System.Func<Vessel, double> distanceOverrideForTesting;
        private readonly List<RecordingAnchorCandidate> backgroundAnchorCandidateBuffer =
            new List<RecordingAnchorCandidate>();
        private readonly HashSet<string> backgroundAnchorVisitedRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);

        public BackgroundRecorder(RecordingTree tree)
        {
            this.tree = tree;

            // Initialize on-rails state for each existing background vessel.
            // Skip destroyed recordings: a sealed recording must not be in onRailsStates
            // (the UpdateOnRails / FinalizeAllForCommit secondary guards would catch
            // it, but the invariant "destroyed recordings are not in BG-tracking
            // structures" is cleaner). This also covers legacy saves that may have
            // left a Destroyed-terminated recording in BackgroundMap.
            foreach (var kvp in tree.BackgroundMap)
            {
                uint vesselPid = kvp.Key;
                string recordingId = kvp.Value;

                if (IsBackgroundRecordingDestroyed(recordingId)) continue;

                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
            }
        }

        internal double LastBackgroundStateDriftCheckUTForTesting =>
            lastBackgroundStateDriftCheckUT;

        #region Public API

        /// <summary>
        /// Called from ParsekFlight.Update() once per frame.
        /// Updates ExplicitEndUT for on-rails background vessels.
        /// </summary>
        public void UpdateOnRails(double currentUT)
        {
            if (tree == null) return;
            WarnIfBackgroundStateDrift(currentUT, "UpdateOnRails");

            // Dictionary is not modified during this loop, safe to iterate directly
            foreach (var kvp in onRailsStates)
            {
                uint pid = kvp.Key;
                var state = kvp.Value;

                // Defense-in-depth: a destroyed recording must never have its
                // ExplicitEndUT or finalization cache overwritten by a periodic
                // background tick. The primary fix retires destroyed records from
                // onRailsStates at OnBackgroundVesselGoOnRails time, so this guard
                // is a backstop for any code path that injects a destroyed record
                // into onRailsStates without going through that seam.
                Recording treeRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out treeRec)
                    && treeRec.VesselDestroyed)
                {
                    continue;
                }

                RefreshOnRailsFinalizationCache(state, currentUT, "background_on_rails_periodic", force: false);

                // Update ExplicitEndUT periodically
                if (currentUT - state.lastExplicitEndUpdate >= ExplicitEndUpdateInterval)
                {
                    if (treeRec != null)
                    {
                        treeRec.ExplicitEndUT = currentUT;
                        state.lastExplicitEndUpdate = currentUT;
                        ParsekLog.VerboseRateLimited("BgRecorder", $"onRailsEndUT.{pid}",
                            $"On-rails ExplicitEndUT updated: pid={pid} UT={currentUT:F1}", 30.0);
                    }
                }
            }
        }

        /// <summary>
        /// Called from PhysicsFramePatch.Postfix for each non-active loaded vessel.
        /// Handles loaded/physics mode recording for background vessels.
        /// </summary>
        public void OnBackgroundPhysicsFrame(Vessel bgVessel)
        {
            if (tree == null || bgVessel == null) return;

            uint pid = bgVessel.persistentId;

            // Check if this vessel is in the tree's BackgroundMap
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId)) return;

            bool hasLoadedState = loadedStates.ContainsKey(pid);
            bool hasOnRailsState = onRailsStates.ContainsKey(pid);
            bool hasRecording = tree.Recordings != null
                && !string.IsNullOrEmpty(recordingId)
                && tree.Recordings.ContainsKey(recordingId);
            if (!hasRecording)
            {
                WarnBackgroundVesselStateDrift(
                    pid,
                    recordingId,
                    "missing-recording",
                    bgVessel,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording);
                return;
            }

            // Defense-in-depth: a destroyed recording must not be sampled by per-frame
            // BG ticks. The primary fix retires destroyed records from BackgroundMap
            // at OnBackgroundVesselGoOnRails time, so this guard catches the unusual
            // case where a recording was marked Destroyed elsewhere without retiring
            // its BG entry.
            if (tree.Recordings[recordingId].VesselDestroyed)
                return;

            // Only process loaded/physics vessels (not packed).
            // Load-bearing for the eccentric-orbit invariant: this early-return is the
            // sole reason on-rails BG vessels do not run `EnvironmentHysteresis.Update`
            // and therefore do not emit Atmospheric<->ExoBallistic TrackSection toggles
            // for periapsis grazes. Removing it would cause `RecordingOptimizer` to
            // accumulate one split per orbit on long-coasting vessels — see
            // `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16.
            if (bgVessel.packed)
            {
                if (!hasOnRailsState)
                {
                    WarnBackgroundVesselStateDrift(
                        pid,
                        recordingId,
                        "packed-without-on-rails-state",
                        bgVessel,
                        hasLoadedState,
                        hasOnRailsState,
                        hasRecording);
                }
                return;
            }

            // Look up the loaded state (created by OnBackgroundVesselGoOffRails)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(pid, out state))
            {
                WarnBackgroundVesselStateDrift(
                    pid,
                    recordingId,
                    "loaded-without-loaded-state",
                    bgVessel,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording);
                return;
            }

            // Get the tree recording
            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            double ut = Planetarium.GetUniversalTime();

            // Poll part events (always, regardless of sampling rate)
            PollPartEvents(bgVessel, state, treeRec, ut);
            RefreshFinalizationCacheForVessel(bgVessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic", force: false);

            // Check for environment transitions (always, regardless of sampling rate)
            HandleBackgroundEnvironmentTransition(state, bgVessel, pid, ut);

            ProximitySamplingTier debrisTier = UpdateDebrisProximityState(state, treeRec, bgVessel);

            UpdateBackgroundAnchorDetection(state, bgVessel, treeRec, ut);

            // Compute distance to focused vessel and determine proximity-based sample interval
            double distance = ComputeDistanceToFocusedVessel(bgVessel);
            double rawProximityInterval = ProximityRateSelector.GetSampleInterval(distance);
            double proximityInterval = ResolveDebrisAwareSampleInterval(rawProximityInterval, treeRec);
            bool highFidelityActive = IsBackgroundHighFidelitySamplingActive(state, ut, distance);
            // Log when sample rate changes for this vessel
            UpdateBackgroundSampleInterval(state, pid, distance, rawProximityInterval, proximityInterval);

            // Skip trajectory sampling if out of range
            if (ShouldSkipTrajectorySamplingForProximity(
                    proximityInterval,
                    highFidelityActive,
                    debrisTier))
            {
                NoteProximitySilenceTick(state, ut);
                return;
            }
            // Back in range (or a high-fidelity / debris tier overrides the range): the
            // designed silence ends on this tick, whether or not a sample follows.
            CloseProximitySilence(state, ut);

            // A FixedUpdate-step callback reads a pre-physics vessel against post-advance
            // bodies; the Update-step read at the end of the render frame samples instead
            // (see PostPhysicsPoseCache).
            if (PostPhysicsPoseCache.ShouldSkipBackgroundSampleInFixedStep(
                    PostPhysicsPoseCache.ReadInFixedTimeStep(),
                    PostPhysicsPoseCache.ReadStatsRunInUpdate(),
                    bgVessel.packed))
            {
                NoteBackgroundFixedStepSampleSkip(pid, ut);
                return;
            }

            // Adaptive sampling (velocity-based). Normal background sampling uses the
            // proximity tier as its min floor; high-fidelity proximity uses the player's
            // configured foreground minimum so close split ghosts follow the same density
            // policy as the active recorder instead of a hard-coded physics-frame rate.
            Vector3 currentVelocity = (Vector3)(bgVessel.rb_velocityD + Krakensbane.GetFrameVelocity());
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(bgVessel.transform.rotation);

            float maxSampleInterval = ParsekSettings.Current?.maxSampleInterval ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float minSampleInterval = ParsekSettings.Current?.minSampleInterval ?? ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
            float velocityDirThreshold = ParsekSettings.Current?.velocityDirThreshold ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speedChangeThreshold = (ParsekSettings.Current?.speedChangeThreshold ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;
            float effectiveMinSampleInterval;
            float effectiveMaxSampleInterval;
            ResolveBackgroundEffectiveSampleIntervals(
                debrisTier,
                highFidelityActive,
                proximityInterval,
                minSampleInterval,
                maxSampleInterval,
                treeRec,
                out effectiveMinSampleInterval,
                out effectiveMaxSampleInterval);

            bool motionTriggered;
            bool attitudeTriggered;
            ResolveBackgroundSampleTriggers(
                state,
                ut,
                currentVelocity,
                currentWorldRotation,
                highFidelityActive,
                effectiveMinSampleInterval,
                effectiveMaxSampleInterval,
                minSampleInterval,
                velocityDirThreshold,
                speedChangeThreshold,
                out motionTriggered,
                out attitudeTriggered);

            if (!motionTriggered && !attitudeTriggered && !state.physicsSoiSamplePending)
            {
                return;
            }

            bool preferRootPartSurfacePose = ShouldPreferRootPartSurfacePoseForBackgroundSample(treeRec);
            TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(
                bgVessel,
                ut,
                currentVelocity,
                preferRootPartSurfacePose: preferRootPartSurfacePose);
            // One section holds one body: a sample on a new body (an SOI change while this
            // vessel is in physics) closes the section here and becomes the next section's
            // first frame.
            SplitBackgroundSectionIfPhysicsBodyChanged(state, treeRec, bgVessel, point);
            state.physicsSoiSamplePending = false;
            TrajectoryPoint absolutePoint = point;
            bool relativeApplied = ApplyBackgroundRelativeOffset(state, ref point, bgVessel, ut);
            LogFirstDebrisOrdinarySampleDiagnostics(
                state,
                treeRec,
                bgVessel,
                absolutePoint,
                point,
                relativeApplied,
                ut);

            // Phase 7 (design doc §13, §18 Phase 7): for surface sections
            // recorded by the loaded-background sampler, capture per-sample
            // ground clearance so playback applies continuous terrain
            // correction at render time. Mirrors `FlightRecorder.CommitRecordedPoint`
            // for parity (a rover that drops out of focus and continues
            // recording in background must keep emitting v9-shape clearance,
            // not silently fall through to NaN). Mutates `point` BEFORE the
            // flat list / section append so both stores see the same value.
            // Non-surface / RELATIVE-frame / missing-PQS keep NaN, the
            // sentinel for the legacy altitude path.
            ApplySurfaceClearanceToPoint(state, bgVessel, ref point);
            // When the active section is RELATIVE, also apply
            // surface clearance to the body-fixed shadow that gets persisted
            // in bodyFixedFrames. The shadow carries body-fixed lat/lon/alt
            // and the body-fixed-primary playback path needs
            // recordedGroundClearance for v9 terrain correction; without
            // this, parent-anchored surface debris replays at raw recorded
            // altitude.
            if (relativeApplied)
            {
                ApplySurfaceClearanceToBodyFixedShadow(state, bgVessel, ref absolutePoint);
            }

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;
            state.lastWorldRotation = currentWorldRotation;
            state.hasLastWorldRotation = true;

            // Dual-write: also add to current TrackSection's frames list. The
            // per-sample warp flag is appended in lockstep inside
            // AddFrameToActiveTrackSection so each large gap is classified by the
            // warp state at its bounding samples at section close.
            AddFrameToActiveTrackSection(
                state,
                point,
                relativeApplied ? (TrajectoryPoint?)absolutePoint : null);

            // Update ExplicitEndUT
            treeRec.ExplicitEndUT = ut;

            ParsekLog.VerboseRateLimited("BgRecorder", $"bgPhysics.{pid}",
                $"Background point sampled: pid={pid} pts={treeRec.Points.Count} " +
                $"alt={bgVessel.altitude:F0} dist={distance:F0}m interval={FormatInterval(proximityInterval)} " +
                $"highFidelity={highFidelityActive} reason={state.highFidelitySamplingReason ?? "(none)"}", 5.0);
        }

        /// <summary>
        /// The per-tick proximity interval bookkeeping of
        /// <see cref="OnBackgroundPhysicsFrame(Vessel)"/>: when the interval differs from the
        /// one the state last used, logs the change (with the debris cap when it moved the raw
        /// tier) and stores it. Allocates only on a change.
        /// </summary>
        private static void UpdateBackgroundSampleInterval(
            BackgroundVesselState state,
            uint pid,
            double distance,
            double rawProximityInterval,
            double proximityInterval)
        {
            if (proximityInterval != state.currentSampleInterval)
            {
                float newHz = (proximityInterval > 0 && proximityInterval < ProximityRateSelector.OutOfRangeInterval)
                    ? (float)(1.0 / proximityInterval)
                    : 0f;
                string distStr = distance.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                string hzStr = newHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                bool capApplied = proximityInterval != rawProximityInterval;
                string capSuffix = capApplied
                    ? $" debris-cap=applied (rawTier={FormatInterval(rawProximityInterval)})"
                    : string.Empty;
                ParsekLog.Info("BgRecorder",
                    $"Sample rate changed: pid={pid} dist={distStr}m " +
                    $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz){capSuffix}");
                state.currentSampleInterval = proximityInterval;
            }
        }

        /// <summary>
        /// Counts and rate-limit logs a fixed-step physics callback whose background sample is
        /// deferred to the Update step (see PostPhysicsPoseCache).
        /// </summary>
        internal void NoteBackgroundFixedStepSampleSkip(uint pid, double ut)
        {
            fixedStepSampleSkipCount++;
            ParsekLog.VerboseRateLimited("BgRecorder", "bg-fixed-step-skip",
                $"Fixed-step physics callback: background sample deferred to the Update step " +
                $"pid={pid} ut={ut.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"skippedCallbacks={fixedStepSampleSkipCount}",
                5.0);
        }

        /// <summary>
        /// The effective adaptive-sampling floor and ceiling of a loaded background tick: a
        /// debris tier uses the debris cadence, high fidelity the configured foreground floor,
        /// and otherwise the proximity interval is the floor; a non-debris tick also takes the
        /// debris-aware ceiling cap.
        /// </summary>
        internal static void ResolveBackgroundEffectiveSampleIntervals(
            ProximitySamplingTier debrisTier,
            bool highFidelityActive,
            double proximityInterval,
            float minSampleInterval,
            float maxSampleInterval,
            Recording treeRec,
            out float effectiveMinSampleInterval,
            out float effectiveMaxSampleInterval)
        {
            effectiveMinSampleInterval = debrisTier != ProximitySamplingTier.None
                ? FlightRecorder.ResolveEffectiveMinSampleInterval(
                    ProximitySamplingTier.None,
                    debrisTier,
                    highFidelityActive,
                    minSampleInterval,
                    maxSampleInterval)
                : highFidelityActive
                    ? FlightRecorder.ResolveEffectiveMinSampleInterval(true, minSampleInterval)
                    : (float)proximityInterval;
            effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                ProximitySamplingTier.None,
                debrisTier,
                highFidelityActive,
                maxSampleInterval,
                minSampleInterval);
            if (debrisTier == ProximitySamplingTier.None)
            {
                effectiveMaxSampleInterval = ResolveDebrisAwareMaxSampleInterval(
                    effectiveMaxSampleInterval,
                    treeRec);
            }
        }

        /// <summary>
        /// The two sample triggers of a loaded background tick against the state's last
        /// recorded sample: motion (velocity direction / speed change or the interval bounds)
        /// and attitude (rotation past the background threshold, floored by the attitude
        /// minimum interval).
        /// </summary>
        private static void ResolveBackgroundSampleTriggers(
            BackgroundVesselState state,
            double ut,
            Vector3 currentVelocity,
            Quaternion currentWorldRotation,
            bool highFidelityActive,
            float effectiveMinSampleInterval,
            float effectiveMaxSampleInterval,
            float minSampleInterval,
            float velocityDirThreshold,
            float speedChangeThreshold,
            out bool motionTriggered,
            out bool attitudeTriggered)
        {
            motionTriggered = TrajectoryMath.ShouldRecordPoint(currentVelocity, state.lastRecordedVelocity,
                ut, state.lastRecordedUT,
                effectiveMinSampleInterval, effectiveMaxSampleInterval,
                velocityDirThreshold, speedChangeThreshold);
            float attitudeMinSampleInterval = ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive,
                effectiveMinSampleInterval,
                minSampleInterval);
            attitudeTriggered = FlightRecorder.ShouldRecordAttitudePoint(
                currentWorldRotation,
                state.lastWorldRotation,
                ut,
                state.lastRecordedUT,
                state.hasLastWorldRotation,
                attitudeMinSampleInterval,
                backgroundAttitudeSampleThresholdDegrees);
        }

        /// <summary>
        /// Queues the focused parent's split-time pose for a debris child seed.
        /// </summary>
        internal void QueueDebrisSeedParentAnchorPoint(
            uint vesselPid,
            string parentRecordingId,
            TrajectoryPoint parentAnchorPoint)
        {
            if (vesselPid == 0u || string.IsNullOrWhiteSpace(parentRecordingId))
                return;

            pendingDebrisSeedParentAnchorPoints[vesselPid] = (parentRecordingId, parentAnchorPoint);
            ParsekLog.Verbose("BgRecorder",
                $"Queued debris seed parent anchor point: pid={vesselPid} " +
                $"parentRecId={parentRecordingId} " +
                $"anchorUT={parentAnchorPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"flags={parentAnchorPoint.flags}");
        }

        /// <summary>
        /// Called when a vessel is added to the background map (transition or branch creation).
        /// </summary>
        public void OnVesselBackgrounded(uint vesselPid, InheritedEngineState? inherited = null,
            SegmentEnvironment? initialEnvironmentOverride = null,
            TrajectoryPoint? initialTrajectoryPoint = null,
            RecordingFinalizationCache inheritedFinalizationCache = null)
        {
            if (tree == null) return;

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: do not initialize BG tracking state for an
            // already-destroyed recording — the recording is sealed and any BG
            // sampling/cache refresh from here would mutate its terminal state.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.backgrounded.{vesselPid}",
                    $"OnVesselBackgrounded: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            AdoptInheritedFinalizationCache(vesselPid, recordingId, inheritedFinalizationCache);

            if (initialEnvironmentOverride.HasValue)
            {
                pendingInitialEnvironmentOverrides[vesselPid] = initialEnvironmentOverride.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"Queued initial environment override for pid={vesselPid}: {initialEnvironmentOverride.Value}");
            }

            if (initialTrajectoryPoint.HasValue)
            {
                pendingInitialTrajectoryPoints[vesselPid] = initialTrajectoryPoint.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"Queued initial trajectory point for pid={vesselPid}: " +
                    $"ut={initialTrajectoryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            // Remove any stale state from a prior initialization (e.g. constructor
            // created on-rails state before this method is called for the same vessel).
            // Close any open orbit segment first so data is not lost.
            BackgroundOnRailsState staleRails;
            if (onRailsStates.TryGetValue(vesselPid, out staleRails) && staleRails.hasOpenOrbitSegment)
            {
                double ut = Planetarium.GetUniversalTime();
                CloseOrbitSegment(staleRails, ut);
            }
            onRailsStates.Remove(vesselPid);
            loadedStates.Remove(vesselPid);
            railsSpanPartStates.Remove(vesselPid);

            // Look up the vessel
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null)
            {
                // Vessel not found yet; create minimal on-rails state
                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (not found): pid={vesselPid} recId={recordingId}");
                return;
            }

            if (v.loaded && !v.packed)
            {
                // Loaded/physics mode
                InitializeLoadedState(v, vesselPid, recordingId, inherited);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                    "backgrounded_loaded", force: true);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (loaded/physics): pid={vesselPid} recId={recordingId}");
            }
            else
            {
                // On-rails mode
                InitializeOnRailsState(v, vesselPid, recordingId);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                    "backgrounded_on_rails", force: true);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (on-rails): pid={vesselPid} recId={recordingId}");
            }
        }

        /// <summary>
        /// Called when a vessel is removed from the background map (promotion, terminal event).
        /// </summary>
        public void OnVesselRemovedFromBackground(uint vesselPid)
        {
            OnVesselRemovedFromBackground(vesselPid, null);
        }

        private void OnVesselRemovedFromBackground(uint vesselPid, double? closeUT)
        {
            // When supplied for a live vessel, closeUT must be from the same frame
            // as the pose sampled below so the boundary point stays coherent.
            double? ut = null;
            if (closeUT.HasValue && IsFiniteUT(closeUT.Value))
                ut = closeUT.Value;

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(vesselPid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    if (!ut.HasValue) ut = Planetarium.GetUniversalTime();
                    CloseOrbitSegment(railsState, ut.Value);
                }
                onRailsStates.Remove(vesselPid);
            }

            // Close TrackSection and flush if loaded
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(vesselPid, out loadedState))
            {
                if (!ut.HasValue) ut = Planetarium.GetUniversalTime();
                CloseBackgroundTrackSection(loadedState, ut.Value);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);

                    // Sample a final boundary point
                    Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                    if (v != null)
                    {
                        SampleBoundaryPoint(v, flushRec, ut.Value);
                    }

                    DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                        flushRec,
                        $"OnVesselRemovedFromBackground pid={vesselPid}");
                }
                loadedStates.Remove(vesselPid);
            }

            pendingInitialEnvironmentOverrides.Remove(vesselPid);
            pendingInitialTrajectoryPoints.Remove(vesselPid);
            pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
            railsSpanPartStates.Remove(vesselPid);
            finalizationCaches.Remove(vesselPid);

            // A vessel leaving the background (promotion to the foreground recorder,
            // merge, switch-segment consume) must not keep a debris TTL: CheckDebrisTTL
            // would later report a TTL close for a recording it no longer owns.
            // EndDebrisRecording removes the entry itself before calling here.
            double cancelledExpiry;
            if (debrisTTLExpiry.TryGetValue(vesselPid, out cancelledExpiry))
            {
                debrisTTLExpiry.Remove(vesselPid);
                ParsekLog.Info("BgRecorder",
                    "Debris TTL cancelled: pid=" + vesselPid +
                    " expiryUT=" + cancelledExpiry.ToString("F1", CultureInfo.InvariantCulture) +
                    " reason=removed-from-background");
            }

            ParsekLog.Info("BgRecorder", $"Vessel removed from background: pid={vesselPid}");
        }

        /// <summary>
        /// Called when a background vessel goes on rails.
        /// Transitions from loaded/physics mode to on-rails mode.
        ///
        /// <para>
        /// Identity-loss override: before any on-rails state is initialized or any
        /// cache refresh fires, check whether the recorded controllable identity has
        /// survived into the live remnant. KSP sets <c>vessel.situation = LANDED</c>
        /// purely from terrain proximity, so a destructive breakup that leaves a
        /// 1-part decoupler at non-trivial surface speed would otherwise be classified
        /// as <c>terminal=Landed</c> by the FinalizerCache short-circuit. When
        /// <see cref="IdentityLossClassifier.IsRecordedIdentityLost"/> fires, we mark
        /// the recording <c>Destroyed</c> through the centralized hygiene helper —
        /// the subsequent cache refresh then short-circuits via the
        /// already-classified-destroyed skip path, preserving the correct terminal.
        /// </para>
        /// </summary>
        public void OnBackgroundVesselGoOnRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Out-of-band destroyed-recording guard. The identity-loss override below
            // handles the normal path (active recording crashes destructively while BG),
            // but any other code that left an already-Destroyed recording in BackgroundMap
            // is short-circuited here so it cannot mutate the sealed terminal verdict.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.go_on_rails.{pid}",
                    $"OnBackgroundVesselGoOnRails: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            // If the vessel was in loaded/physics mode, transition to on-rails
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                SegmentEnvironment nextEnv = ClassifyCurrentBackgroundEnvironment(v, loadedState.cachedEngines);
                bool willHavePlayableOnRailsPayload = WillInitializeOnRailsWithPlayablePayload(v);
                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushLoadedStateForOnRailsTransition(
                        loadedState,
                        flushRec,
                        nextEnv,
                        willHavePlayableOnRailsPayload,
                        CreateTrajectoryPointFromVessel(v, ut),
                        ut);
                }

                // M6: the foreground recorder emits terminal EngineShutdown / RCSStopped /
                // RoboticMotionStopped at its own rails transition
                // (FlightRecorder.EmitTerminalEventsAndClearActiveState). The background half did
                // trajectory work and then dropped loadedStates outright, so a BG ghost that packed
                // mid-burn kept its plume lit for the entire rails span. Emit the same terminals,
                // then snapshot the (now engine-quiet) tracking state so the off-rails re-entry can
                // diff against it instead of silently re-syncing.
                EmitBackgroundRailsTerminalEvents(loadedState, ut);
                CaptureRailsSpanPartStates(pid, loadedState);

                loadedStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition loaded→on-rails: pid={pid}");
            }

            // Identity-loss override runs BEFORE InitializeOnRailsState and BEFORE
            // the cache refresh. On a positive identity-loss verdict the recording
            // is sealed at MarkDestroyedAtTerminal-time AND retired from BG tracking
            // (removed from BackgroundMap + onRailsStates + finalizationCaches).
            // Retirement is load-bearing: without it, the destroyed recording's
            // ExplicitEndUT would be overwritten by UpdateOnRails (per-frame
            // periodic update) and FinalizeAllForCommit (per-BackgroundMap-entry
            // bulk update), moving the crash endpoint from the identity-loss UT
            // to the eventual commit/latest-remnant UT — which downstream
            // GhostPlaybackLogic uses to time the non-debris destroyed explosion.
            Recording onRailsRec;
            if (tree.Recordings.TryGetValue(recordingId, out onRailsRec)
                && !onRailsRec.VesselDestroyed
                && IdentityLossClassifier.IsRecordedIdentityLost(onRailsRec, v))
            {
                int recordedControllerCount = onRailsRec.Controllers?.Count ?? 0;
                int survivingPartCount = v.parts?.Count ?? 0;
                ParsekLog.Info("BgRecorder",
                    $"Recorded controllable identity lost on go-on-rails: pid={pid} " +
                    $"recordedControllers={recordedControllerCount} survivingParts={survivingPartCount} " +
                    $"sit={v.situation} rec={recordingId} — classifying as Destroyed");
                onRailsRec.MarkDestroyedAtTerminal(ut, "BgRecorder.OnBackgroundVesselGoOnRails");
                RetireDestroyedBackgroundEntry(pid, recordingId, ut);
                return;
            }

            // Initialize on-rails state
            InitializeOnRailsState(v, pid, recordingId);
            RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                "background_go_on_rails", force: true);
        }

        /// <summary>
        /// M6 half one: emits the terminal EngineShutdown / RCSStopped / RoboticMotionStopped events
        /// a BG vessel owes its recording when it packs onto rails, then clears the corresponding
        /// active-state tracking so the snapshot taken immediately afterwards agrees with what the
        /// recording now says.
        ///
        /// <para>
        /// This is the exact counterpart of <c>FlightRecorder.EmitTerminalEventsAndClearActiveState</c>,
        /// which the foreground recorder has always run at its own on-rails transition. Without it a
        /// BG ghost that packed mid-burn replayed with its plume lit for the whole rails span,
        /// because nothing ever told the recording the burn ended.
        /// </para>
        /// <para>
        /// A vessel that packs with nothing running emits nothing at all, so a parked rover's
        /// boring tail is not extended: <c>EmitTerminalEngineAndRcsEvents</c> iterates the ACTIVE
        /// sets, and EngineShutdown is non-inert for <c>FindLastInterestingUT</c> only when one is
        /// actually written.
        /// </para>
        /// </summary>
        private void EmitBackgroundRailsTerminalEvents(BackgroundVesselState loadedState, double ut)
        {
            if (loadedState == null) return;

            Recording rec;
            if (tree?.Recordings == null
                || string.IsNullOrEmpty(loadedState.recordingId)
                || !tree.Recordings.TryGetValue(loadedState.recordingId, out rec)
                || rec == null)
                return;

            var terminalEvents = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                loadedState.activeEngineKeys, loadedState.activeRcsKeys, loadedState.activeRoboticKeys,
                loadedState.lastRoboticPosition, ut, "BgRecorder");

            if (terminalEvents.Count > 0)
            {
                rec.PartEvents.AddRange(terminalEvents);
                rec.MarkFilesDirty();
                ParsekLog.Info("BgRecorder",
                    $"Emitted {terminalEvents.Count} terminal FX event(s) at bg on-rails transition " +
                    $"(pid={loadedState.vesselPid} rec={loadedState.recordingId} " +
                    $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)})");
            }

            loadedState.activeEngineKeys.Clear();
            loadedState.activeRcsKeys.Clear();
            loadedState.activeRoboticKeys.Clear();
            loadedState.lastThrottle.Clear();
            loadedState.lastRcsThrottle.Clear();
            loadedState.lastRoboticPosition.Clear();
            loadedState.rcsActiveFrameCount.Clear();
        }

        /// <summary>
        /// M6 half two (capture side): deep-copies the loaded state's part-tracking sets so the
        /// off-rails re-entry can diff against what the recording believes, rather than declining to
        /// write anything because the recording already has events.
        /// </summary>
        private void CaptureRailsSpanPartStates(uint pid, BackgroundVesselState loadedState)
        {
            if (loadedState == null) return;
            railsSpanPartStates[pid] = PartStateSeeder.ClonePartTrackingSets(
                BuildPartTrackingSetsFromState(loadedState));
            ParsekLog.Verbose("BgRecorder",
                $"Captured rails-span part state for pid={pid} rec={loadedState.recordingId}");
        }

        /// <summary>
        /// Returns true when the BG-tracked recording for <paramref name="recordingId"/>
        /// is already marked Destroyed. Every BG entrypoint that takes a Vessel
        /// (go-on-rails / go-off-rails / SOI / will-destroy / part-die / part-joint-break /
        /// vessel-backgrounded) calls this immediately after the
        /// <see cref="RecordingTree.BackgroundMap"/> lookup to skip out-of-band
        /// destroyed records that didn't go through the identity-loss
        /// <see cref="RetireDestroyedBackgroundEntry"/> retirement seam. Without this
        /// guard, a destroyed record left in <see cref="RecordingTree.BackgroundMap"/>
        /// could still be sampled, get its <see cref="Recording.ExplicitEndUT"/>
        /// advanced, accept new <see cref="PartEvent"/>s, or have an open orbit
        /// segment closed at a later UT — all of which would shift the
        /// destruction-explosion timing downstream in <see cref="GhostPlaybackLogic"/>.
        ///
        /// <para>The predicate reads <see cref="Recording.VesselDestroyed"/> and NOTHING
        /// else, which is what keeps an EVA-construction pocket sampling normally right
        /// up to its destroy frame: <c>ApplyDisassembledTerminal</c> deliberately leaves
        /// that flag false, so a Disassembled recording is not "destroyed" here and its
        /// accumulated TrackSections still get flushed and persisted by
        /// <see cref="OnBackgroundVesselWillDestroy"/>. Internal so a headless cell can
        /// drive that, rather than only asserting the flag at the stamp.</para>
        /// </summary>
        internal bool IsBackgroundRecordingDestroyed(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || tree == null || tree.Recordings == null)
                return false;
            Recording rec;
            return tree.Recordings.TryGetValue(recordingId, out rec)
                && rec != null
                && rec.VesselDestroyed;
        }

        /// <summary>
        /// Removes a destroyed recording from every BG-tracking data structure so
        /// later <see cref="UpdateOnRails"/>, <see cref="FinalizeAllForCommit"/>, and
        /// <see cref="OnBackgroundPhysicsFrame"/> iterations cannot touch its
        /// <see cref="Recording.ExplicitEndUT"/> or sample the inert remnant. Called
        /// after <see cref="Recording.MarkDestroyedAtTerminal"/> seals the verdict.
        ///
        /// <para>
        /// Drains the four primary BG-state dicts (<c>BackgroundMap</c>,
        /// <c>onRailsStates</c>, <c>loadedStates</c>, <c>finalizationCaches</c>) plus
        /// the per-parent split-detection dicts (<c>pendingBackgroundSplitChecks</c>,
        /// <c>preBreakVesselPidSnapshots</c>) — both populated by
        /// <see cref="OnBackgroundPartJointBreak"/> for any non-debris BG parent
        /// that suffers a joint break, so a parent that experiences a joint break
        /// followed by go-on-rails identity loss on the same/next frame would
        /// otherwise leave entries that <see cref="ProcessPendingSplitChecks"/>
        /// could use to create branch points and child recordings off the sealed
        /// parent. Debris-only dicts (<c>pendingDebrisSeedParentAnchorPoints</c>,
        /// <c>debrisTTLExpiry</c>) are not drained here because the identity-loss
        /// override only fires on non-debris recordings (<c>IsDebris == false</c>)
        /// and those dicts are populated only on the debris paths.
        /// </para>
        /// <para>
        /// Logged at Verbose to avoid doubling Info-level output: this is a
        /// downstream consequence of the Info-level
        /// <see cref="Recording.MarkDestroyedAtTerminal"/> entry that fires
        /// immediately before.
        /// </para>
        /// </summary>
        internal void RetireDestroyedBackgroundEntry(uint pid, string recordingId, double terminalUT)
        {
            tree.BackgroundMap.Remove(pid);
            onRailsStates.Remove(pid);
            loadedStates.Remove(pid);
            railsSpanPartStates.Remove(pid);
            finalizationCaches.Remove(pid);
            // Drain the deferred-split detection dicts keyed on this parent pid so
            // ProcessPendingSplitChecks → HandleBackgroundVesselSplit cannot fire
            // for a sealed parent next frame.
            pendingBackgroundSplitChecks.Remove(pid);
            preBreakVesselPidSnapshots.Remove(pid);
            ParsekLog.Verbose("BgRecorder",
                $"Retired BG tracking for destroyed remnant: pid={pid} rec={recordingId} " +
                $"terminalUT={terminalUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Called when a background vessel goes off rails.
        /// Transitions from on-rails mode to loaded/physics mode.
        /// </summary>
        public void OnBackgroundVesselGoOffRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Destroyed-recording guard: a destroyed remnant that later unpacks
            // (player switches focus to it) must not get a loaded-state
            // initialization or a boundary point sampled — that would append a
            // post-terminal point to the sealed recording and shift the destroyed
            // explosion timing in GhostPlaybackLogic.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.go_off_rails.{pid}",
                    $"OnBackgroundVesselGoOffRails: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition on-rails→loaded: pid={pid}");
            }

            // Initialize loaded/physics state if vessel is unpacked
            if (!v.packed)
            {
                InitializeLoadedState(v, pid, recordingId);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                    "background_go_off_rails", force: true);

                // Sample a boundary point
                Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    SampleBoundaryPoint(v, treeRec, ut);
                }
            }
        }

        /// <summary>
        /// Called when a background vessel changes SOI. On rails: closes the orbit segment in
        /// the old SOI and opens one in the new SOI. Loaded (in physics): arms a sample, and
        /// the per-frame sampler splits the track section at the first sample on the new body
        /// (BackgroundRecorder.PhysicsSoi.cs).
        /// </summary>
        public void OnBackgroundVesselSOIChanged(Vessel v, CelestialBody fromBody)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Destroyed-recording guard: a destroyed remnant's SOI change is moot —
            // do not close / open orbit segments or refresh the finalization cache.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.soi_change.{pid}",
                    $"OnBackgroundVesselSOIChanged: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
            {
                // Close the old orbit segment
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, ut);
                }

                // Open a new orbit segment with the new body's orbit
                if (v.orbit != null)
                {
                    state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                    LogBackgroundReFlyOrbitSegmentVelocityNonFinite(
                        recordingId,
                        v,
                        ut,
                        "soi-change");
                    state.hasOpenOrbitSegment = true;
                }
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                    "background_soi_change", force: true);

                ParsekLog.Info("BgRecorder", $"SOI change for background vessel: pid={pid} " +
                    $"from={fromBody?.name ?? "?"} to={v.mainBody?.name ?? "?"}");
                return;
            }

            // Loaded (a formation flight through the boundary with the active vessel): no
            // orbit segment to roll over; the sampler splits the section at the first sample
            // on the new body (SplitBackgroundSectionIfPhysicsBodyChanged).
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
                ArmBackgroundPhysicsSoiSample(loadedState, fromBody?.name, v.mainBody?.name, ut);
        }

        /// <summary>
        /// Called when a background vessel is about to be destroyed.
        /// Closes any open orbit segment, cleans up tracking state.
        /// </summary>
        public void OnBackgroundVesselWillDestroy(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            if (!tree.BackgroundMap.ContainsKey(pid)) return;
            double ut = Planetarium.GetUniversalTime();
            string recordingId;
            tree.BackgroundMap.TryGetValue(pid, out recordingId);

            // Destroyed-recording guard: if the recording is already sealed (e.g.
            // via the identity-loss override that didn't fully retire, or any
            // out-of-band path that called MarkDestroyedAtTerminal), skip the
            // finalization-cache refresh and reuse RetireDestroyedBackgroundEntry
            // for the dictionary drain so this branch is single-source-of-truth
            // with the identity-loss retirement seam (including the
            // pendingBackgroundSplitChecks / preBreakVesselPidSnapshots drain
            // that prevents ProcessPendingSplitChecks from dispatching on a sealed
            // parent). The pending-initial dicts that the live-vessel-destroy
            // fall-through normally drains are also cleared here for symmetry.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                pendingInitialEnvironmentOverrides.Remove(pid);
                pendingInitialTrajectoryPoints.Remove(pid);
                RetireDestroyedBackgroundEntry(pid, recordingId, ut);
                ParsekLog.Info("BgRecorder",
                    $"OnBackgroundVesselWillDestroy: cleared BG state for already-destroyed recording " +
                    $"pid={pid} rec={recordingId}");
                return;
            }

            RefreshFinalizationCacheForVessel(v, recordingId, v.loaded && !v.packed
                ? FinalizationCacheOwner.BackgroundLoaded
                : FinalizationCacheOwner.BackgroundOnRails,
                "background_destroy", force: true);

            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
            }

            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                CloseBackgroundTrackSection(loadedState, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);
                    // Bug #280: persist the flushed data to disk IMMEDIATELY at
                    // finalization time. Relying on OnSave to see FilesDirty and write
                    // the file has proven fragile — the 2026-04-09 playtest showed
                    // destroyed debris recordings consistently losing their 22-frame
                    // TrackSection data across the scene reload because either the
                    // dirty flag or the save path dropped them. Writing here bypasses
                    // OnSave entirely for the destroy path and guarantees the file
                    // exists on disk before any scene transition.
                    PersistFinalizedRecording(flushRec, $"OnBackgroundVesselWillDestroy pid={pid}");
                }
                loadedStates.Remove(pid);
            }

            // Do not apply the cache here. KSP can raise the destroy signal for
            // cases that are later classified as docking/false unloads; ParsekFlight's
            // DeferredDestructionCheck consumes the cache after true destruction is
            // confirmed and persists the sidecar again with terminal metadata.

            Recording terminalRec = null;
            if (!string.IsNullOrEmpty(recordingId))
                tree.Recordings.TryGetValue(recordingId, out terminalRec);
            bool disassembled =
                terminalRec != null
                && terminalRec.TerminalStateValue == TerminalState.Disassembled;

            if (v.vesselType == VesselType.EVA)
                ParsekLog.Info("BgRecorder", $"Background EVA vessel ended: pid={pid}");
            else if (disassembled)
                // ParsekFlight.TryStampDisassembledTerminal ran before this handler
                // (same frame, same event): the vessel's last part was pocketed in EVA
                // construction. A designed outcome belongs on the Info surface, not
                // the WRN one the log validator reads.
                ParsekLog.Info("BgRecorder", $"Background vessel disassembled: pid={pid} rec={recordingId}");
            else
                ParsekLog.Warn("BgRecorder", $"Background vessel destroyed: pid={pid}");

            pendingInitialEnvironmentOverrides.Remove(pid);
            pendingInitialTrajectoryPoints.Remove(pid);
            // The rails-span snapshot dies with the vessel. It is keyed on the vessel pid, and
            // persistentId is craft-baked rather than launch-unique — a later launch of the SAME
            // craft reuses the pid verbatim, so an orphan left here would be handed to that launch's
            // first off-rails re-entry and diffed as though it were the same continuous flight.
            railsSpanPartStates.Remove(pid);
            // Keep the finalization cache until DeferredDestructionCheck confirms
            // whether this was true destruction or a false unload signal.
        }

        /// <summary>
        /// Called on scene change or tree teardown. Closes all open orbit segments
        /// and cleans up all per-vessel state.
        /// </summary>
        public void Shutdown()
        {
            Shutdown(Planetarium.GetUniversalTime());
        }

        /// <summary>
        /// Shutdown with an explicit UT, for tests that exercise the destroyed-record
        /// skip paths without requiring the Unity Planetarium singleton.
        /// </summary>
        internal void Shutdown(double ut)
        {
            UnsubscribePartEvents();

            // Close all open orbit segments (skip destroyed — closing an open segment
            // at shutdown UT would extend the sealed recording past its terminalUT).
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (!state.hasOpenOrbitSegment) continue;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.shutdown_orbit.{state.vesselPid}",
                        $"Shutdown: skipping orbit-segment close for destroyed recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                CloseOrbitSegment(state, ut);
            }

            // Close and flush TrackSections for all loaded vessels (skip destroyed —
            // flushing a TrackSection at shutdown UT and persisting it would similarly
            // extend Recording.EndUT past the sealed identity-loss UT).
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.shutdown_loaded.{state.vesselPid}",
                        $"Shutdown: skipping loaded-state flush for destroyed recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                CloseBackgroundTrackSection(state, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);
                    // Bug #280: persist flushed data immediately (see PersistFinalizedRecording docs).
                    PersistFinalizedRecording(flushRec, $"Shutdown pid={state.vesselPid}");
                }
            }

            onRailsStates.Clear();
            loadedStates.Clear();
            pendingInitialEnvironmentOverrides.Clear();
            pendingInitialTrajectoryPoints.Clear();
            finalizationCaches.Clear();

            ParsekLog.Info("BgRecorder", "Shutdown complete — all background states cleared");
        }

        /// <summary>
        /// Drops all background recording state without flushing or persisting
        /// trajectory data. Used when the caller is intentionally discarding the
        /// entire in-memory attempt, such as Discard Re-Fly loading the origin
        /// rewind point.
        /// </summary>
        public void DiscardWithoutPersist(string reason)
        {
            UnsubscribePartEvents();

            int onRailsCount = onRailsStates.Count;
            int loadedCount = loadedStates.Count;
            int finalizationCacheCount = finalizationCaches.Count;

            onRailsStates.Clear();
            loadedStates.Clear();
            pendingInitialEnvironmentOverrides.Clear();
            pendingInitialTrajectoryPoints.Clear();
            finalizationCaches.Clear();

            ParsekLog.Info("BgRecorder",
                $"DiscardWithoutPersist: cleared background states without flushing " +
                $"onRails={onRailsCount} loaded={loadedCount} caches={finalizationCacheCount} " +
                $"reason='{(string.IsNullOrEmpty(reason) ? "<unspecified>" : reason)}'");
        }

        /// <summary>
        /// Captures an orbital checkpoint for all on-rails background vessels at the given UT.
        /// For each vessel with an open orbit segment, closes the current segment and opens a
        /// fresh one with the vessel's current orbital elements. This creates clean reference
        /// points for orbital propagation during playback.
        /// Called at time warp boundaries and scene changes.
        /// </summary>
        internal void CheckpointAllVessels(double ut)
        {
            CheckpointAllVessels(ut, vesselFinderOverride);
        }

        /// <summary>
        /// Core checkpoint logic with injectable vessel finder for testability.
        /// If vesselFinder is null, uses FlightRecorder.FindVesselByPid.
        /// </summary>
        internal void CheckpointAllVessels(double ut, System.Func<uint, Vessel> vesselFinder)
        {
            if (tree == null) return;

            int checkpointed = 0;
            int skippedNoVessel = 0;
            int skippedNotOrbital = 0;
            int skippedDuplicateBoundary = 0;

            foreach (var kvp in onRailsStates)
            {
                uint vesselPid = kvp.Key;
                var state = kvp.Value;

                // Only checkpoint vessels with open orbit segments (on-rails, orbiting)
                if (!state.hasOpenOrbitSegment)
                {
                    // Landed/splashed or no-orbit vessels don't need orbital checkpoints
                    skippedNotOrbital++;
                    continue;
                }

                // Skip destroyed recordings — closing/opening a fresh orbit segment
                // at checkpoint UT would extend the sealed recording past its
                // identity-loss terminalUT, shifting the destroyed-explosion timing
                // in GhostPlaybackLogic.
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.checkpoint.{vesselPid}",
                        $"CheckpointAllVessels: skipping destroyed recording " +
                        $"pid={vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }

                if (IsDuplicateOrbitSegmentBoundary(state.currentOrbitSegment, ut))
                {
                    skippedDuplicateBoundary++;
                    continue;
                }

                // Find the live vessel to get current orbital elements
                Vessel v = vesselFinder != null
                    ? vesselFinder(vesselPid)
                    : FlightRecorder.FindVesselByPid(vesselPid);

                // Close the current orbit segment at checkpoint UT regardless of vessel availability.
                // The recorded orbital data up to this point is preserved even if the vessel
                // can't be found (e.g., destroyed between frames).
                CloseOrbitSegment(state, ut);

                if (v == null || v.orbit == null)
                {
                    skippedNoVessel++;
                    ParsekLog.Verbose("BgRecorder",
                        $"CheckpointAllVessels: closed segment for pid={vesselPid} but " +
                        $"vessel/orbit not found — no new segment opened");
                    continue;
                }

                // Open a fresh orbit segment with current orbital elements
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                LogBackgroundReFlyOrbitSegmentVelocityNonFinite(
                    state.recordingId,
                    v,
                    ut,
                    "checkpoint");
                state.hasOpenOrbitSegment = true;
                checkpointed++;
            }

            // Bug #592: this summary fires once per onTimeWarpRateChanged, which KSP
            // can re-emit hundreds of times at the same rate during a single session.
            // Rate-limit per "shape" of the result so changes in checkpoint counts still
            // surface (we want to see the moment behavior changes), but identical
            // no-op summaries during a quiet warp burst collapse into one line.
            string key = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "checkpoint-all-{0}-{1}-{2}-{3}",
                checkpointed, skippedNotOrbital, skippedNoVessel, skippedDuplicateBoundary);
            ParsekLog.VerboseRateLimited("BgRecorder", key,
                $"CheckpointAllVessels at UT={ut:F2}: checkpointed={checkpointed}, " +
                $"skippedNotOrbital={skippedNotOrbital}, skippedNoVessel={skippedNoVessel}, " +
                $"skippedDuplicateBoundary={skippedDuplicateBoundary}");
        }

        private static bool IsDuplicateOrbitSegmentBoundary(OrbitSegment segment, double ut)
        {
            const double UTEpsilon = 0.000001;
            return IsFiniteUT(segment.startUT)
                && IsFiniteUT(ut)
                && Math.Abs(segment.startUT - ut) <= UTEpsilon;
        }

        /// <summary>
        /// Pure static method for determining whether an orbital checkpoint should be
        /// captured for a given vessel situation.
        /// </summary>
        internal static bool IsOrbitalCheckpointSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.ORBITING
                || situation == Vessel.Situations.SUB_ORBITAL
                || situation == Vessel.Situations.ESCAPING;
        }

        internal static bool ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
            SegmentEnvironment previousEnv,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload)
        {
            if (willHavePlayableOnRailsPayload)
                return false;

            return RecordingOptimizer.SplitEnvironmentClass(previousEnv)
                != RecordingOptimizer.SplitEnvironmentClass(nextEnv);
        }

        #endregion

        #region Internal State Management

        private static SegmentEnvironment ClassifyCurrentBackgroundEnvironment(
            Vessel v,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines)
        {
            bool hasAtmo = v.mainBody != null && v.mainBody.atmosphere;
            double atmoDepth = hasAtmo ? v.mainBody.atmosphereDepth : 0;
            double approachAlt = (!hasAtmo && v.mainBody != null)
                ? FlightRecorder.ComputeApproachAltitude(v.mainBody) : 0;
            return ClassifyBackgroundEnvironment(
                hasAtmo, v.altitude, atmoDepth,
                (int)v.situation, v.srfSpeed, cachedEngines, approachAlt,
                v.isEVA, v.heightFromTerrain,
                EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                v.mainBody != null && v.mainBody.ocean);
        }

        #endregion


        #region Jettison Name Cache

        /// <summary>
        /// Per-vessel jettison name cache (mirrors FlightRecorder.GetCachedJettisonNames).
        /// </summary>
        private static string[] GetCachedJettisonNames(BackgroundVesselState state, ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (state.jettisonNameRawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                state.parsedJettisonNamesCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = ParseJettisonNames(rawNames);
            state.jettisonNameRawCache[moduleKey] = rawNames;
            state.parsedJettisonNamesCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        // ParseJettisonNames: use FlightRecorder.ParseJettisonNames (internal static)
        private static string[] ParseJettisonNames(string rawNames)
            => FlightRecorder.ParseJettisonNames(rawNames);

        #endregion
    }
}
