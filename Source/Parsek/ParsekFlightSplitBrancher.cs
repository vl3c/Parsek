using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The flight-scene split brancher: what a split, dock, undock, EVA or board does to the active
    // recording tree. Split and merge branch creation (live and background parent), the fallback
    // commit and resume of a split recorder, breakup processing and the crash coalescer tick, the
    // rewind-point authoring for splits, the dock / board / undock confirmations and the bodies of
    // the dock, undock, EVA, board and decouple GameEvents handlers. ParsekFlight keeps the GameEvents
    // subscriptions (its handlers forward here, so Start / OnDestroy add and remove the same
    // delegates), the Deferred* coroutines, the pending-split and decouple bookkeeping other seams
    // read and the split statics other files name; it reaches this service through SplitBrancher.
    internal sealed class ParsekFlightSplitBrancher
    {
        private readonly ParsekFlight host;

        internal ParsekFlightSplitBrancher(ParsekFlight host)
        {
            this.host = host;
        }

        // Docking-port undock handoff (onPartUndock -> onVesselsUndocking, same frame).
        // onPartUndock fires FIRST inside Part.Undock(), while the combined vessel is still
        // intact (ideal moment to capture the departing root part's pre-split origin seed and
        // the structural-event snapshot), but the new vessel's persistentId is not assigned
        // until KSP finishes the split and fires onVesselsUndocking at the end of the same
        // call. We stash the pre-split seed here and let OnVesselsUndocking author the actual
        // Undock branch with the final pids.
        private TrajectoryPoint? pendingUndockRootPartSeed;

        internal static int SafeCrewCount(Vessel v)
        {
            try { return v.GetCrewCount(); }
            catch (Exception ex)
            {
                ParsekLog.Warn("Flight",
                    $"ShouldRecordAsOwnBranch: crew count unreadable for pid={v.persistentId} " +
                    $"({ex.GetType().Name}); treating it as crewless");
                return 0;
            }
        }

        internal static IEnumerable<string> EnumeratePartNames(Vessel v)
        {
            if (v == null || v.parts == null)
                yield break;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                if (p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.name))
                {
                    yield return p.partInfo.name;
                    continue;
                }
                if (!string.IsNullOrEmpty(p.partName))
                {
                    yield return p.partName;
                    continue;
                }
                if (!string.IsNullOrEmpty(p.name))
                    yield return p.name;
            }
        }

        internal static IEnumerable<string> EnumerateModuleNames(Vessel v)
        {
            if (v == null || v.parts == null)
                yield break;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null)
                    continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.IsNullOrEmpty(module.moduleName))
                        yield return module.moduleName;
                    Type moduleType = module.GetType();
                    if (moduleType != null && !string.IsNullOrEmpty(moduleType.Name))
                        yield return moduleType.Name;
                }
            }
        }

        internal static bool ContainsAsteroidOrCometPart(IEnumerable<string> partNames)
        {
            if (partNames == null)
                return false;

            foreach (string partName in partNames)
            {
                if (string.IsNullOrEmpty(partName))
                    continue;
                // Part cfg/runtime names can carry expansion or variant suffixes,
                // while module names below are stable C# class identifiers.
                if (partName.IndexOf("PotatoRoid", StringComparison.OrdinalIgnoreCase) >= 0
                    || partName.IndexOf("PotatoComet", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        internal static bool ContainsAsteroidOrCometModule(IEnumerable<string> moduleNames)
        {
            if (moduleNames == null)
                return false;

            foreach (string moduleName in moduleNames)
            {
                if (string.IsNullOrEmpty(moduleName))
                    continue;
                if (string.Equals(moduleName, "ModuleAsteroid", StringComparison.Ordinal)
                    || string.Equals(moduleName, "ModuleComet", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static RouteEndpoint? BuildRouteEndpointFromVessel(
            Vessel vessel,
            uint expectedVesselPersistentId)
        {
            if (vessel == null || vessel.persistentId != expectedVesselPersistentId)
                return null;

            return new RouteEndpoint
            {
                VesselPersistentId = vessel.persistentId,
                // THE ENDPOINT'S LAUNCH-UNIQUE IDENTITY, STAMPED AT CAPTURE (2026-09-06,
                // ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER). Without it a destination
                // stop's resolution walk is pid -> proximity with the root-part step
                // unreachable BY CONSTRUCTION - and a base with any visitor docked to it
                // presents whichever half stock made dominant, so a DOMINANT VISITOR made the
                // pid step miss, the proximity step land on the composite, and the transfer
                // rebind the route to the visitor, which then flew away with it. With the
                // root id the walk starts at identity and resolves back to the base whichever
                // half dominates. Read HERE because this runs pre-couple, while the endpoint
                // still has its own Vessel: after Part.Couple the merged craft's root is the
                // DOMINANT half's, so a later read could name the wrong vessel.
                RootPartUId = Logistics.RouteEndpointResolver.ResolveRootPartFlightId(vessel),
                // AND ITS LAUNCH GUID, for symmetry with the origin path. The origin
                // endpoint carries the guid its undock bind READ
                // (RouteProofCapture's Stamped arm, RESOLVER-PID-STEP-NOT-GUID-GATED); a
                // destination captured here has the live vessel in hand, so the same key is
                // free and read at the same pre-couple instant as the root id. It only ever
                // NARROWS the resolver's pid step - the gate refuses a match whose guid
                // conclusively differs and falls through to proximity, and an unreadable
                // guid stays null, which is the ungated behaviour every pre-key route has.
                // Not hashed (RouteProofHasher excludes it deliberately), so stamping it
                // moves no existing route to SourceChanged.
                LaunchGuid = Logistics.RouteEndpointTransfer.TryReadLaunchGuid(vessel),
                BodyName = vessel.mainBody != null ? vessel.mainBody.bodyName : null,
                Latitude = vessel.latitude,
                Longitude = vessel.longitude,
                Altitude = vessel.altitude,
                IsSurface = IsRouteEndpointSurfaceSituation(vessel.situation)
            };
        }

        internal static bool TryParseRouteEndpointSituation(
            ConfigNode snapshot,
            out Vessel.Situations situation)
        {
            situation = default(Vessel.Situations);
            string raw = snapshot?.GetValue("sit");
            if (string.IsNullOrEmpty(raw))
                return false;

            if (Enum.TryParse(raw, ignoreCase: true, out situation))
                return true;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) &&
                Enum.IsDefined(typeof(Vessel.Situations), numeric))
            {
                situation = (Vessel.Situations)numeric;
                return true;
            }

            return false;
        }

        internal static bool TryGetSnapshotRouteCoordinate(
            ConfigNode snapshot,
            string primaryKey,
            string fallbackKey,
            out double value)
        {
            return VesselSnapshotOps.TryGetSnapshotDouble(snapshot, primaryKey, out value)
                || VesselSnapshotOps.TryGetSnapshotDouble(snapshot, fallbackKey, out value);
        }

        internal static bool IsRouteEndpointSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED ||
                situation == Vessel.Situations.SPLASHED ||
                situation == Vessel.Situations.PRELAUNCH;
        }

        private void CapturePendingDockRouteEndpointProof(
            GameEvents.FromToAction<Part, Part> data,
            uint routeTargetPid)
        {
            host.pendingDockRouteEndpointAtDock = null;
            host.pendingDockRouteEndpointSituation = -1;

            if (routeTargetPid == 0)
                return;

            Vessel endpointVessel = ResolveDockRouteEndpointVesselFromEvent(data, routeTargetPid);
            RouteEndpoint? endpoint = BuildRouteEndpointFromVessel(endpointVessel, routeTargetPid);
            if (endpoint.HasValue)
            {
                host.pendingDockRouteEndpointAtDock = endpoint;
                host.pendingDockRouteEndpointSituation = endpointVessel != null
                    ? (int)endpointVessel.situation
                    : -1;
                ParsekLog.Verbose("Flight",
                    $"CapturePendingDockRouteEndpointProof: captured endpoint proof targetPid={routeTargetPid} " +
                    $"situation={host.pendingDockRouteEndpointSituation}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"CapturePendingDockRouteEndpointProof: endpoint vessel unavailable at dock targetPid={routeTargetPid}; " +
                "will try background snapshot fallback");
        }

        private static Vessel ResolveDockRouteEndpointVesselFromEvent(
            GameEvents.FromToAction<Part, Part> data,
            uint routeTargetPid)
        {
            Vessel fromVessel = data.from != null ? data.from.vessel : null;
            if (fromVessel != null && fromVessel.persistentId == routeTargetPid)
                return fromVessel;

            Vessel toVessel = data.to != null ? data.to.vessel : null;
            if (toVessel != null && toVessel.persistentId == routeTargetPid)
                return toVessel;

            return FlightRecorder.FindVesselByPid(routeTargetPid);
        }

        internal static string GetMergeCauseForBranchType(BranchPointType branchType)
        {
            switch (branchType)
            {
                case BranchPointType.Dock:
                    return "DOCK";
                case BranchPointType.Board:
                    return "BOARD";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Creates a tree branch for a vessel split event.
        /// Requires an active tree (always present in always-tree mode).
        /// Creates two child recordings (active + background), wires up BackgroundRecorder,
        /// and starts a new FlightRecorder for the active child.
        /// </summary>
        internal void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0,
            TrajectoryPoint? backgroundInitialTrajectoryPoint = null)
        {
            RecorderStateLog.RecState("CreateSplitBranch:entry", host.CaptureRecorderState());
            if (host.pendingSplitRecorder == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no pending split recorder — aborting");
                return;
            }

            var splitRecorder = host.pendingSplitRecorder;

            // Use the current active recording as parent
            string parentRecordingId = host.activeTree.ActiveRecordingId;

            if (parentRecordingId == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no active recording in existing tree — aborting");
                FallbackCommitSplitRecorder(splitRecorder);
                return;
            }

            // Flush captured data into the existing tree recording
            Recording parentRec;
            if (host.activeTree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                ParsekFlight.AppendCapturedDataToRecording(parentRec, splitRecorder.CaptureAtStop, branchUT);

                // Bug #271: in always-tree mode, the root recording was created without
                // a snapshot. Copy from CaptureAtStop on the first split so the ghost
                // mesh has vessel geometry instead of the sphere fallback.
                if (parentRec.VesselSnapshot == null && splitRecorder.CaptureAtStop?.VesselSnapshot != null)
                {
                    parentRec.VesselSnapshot = splitRecorder.CaptureAtStop.VesselSnapshot.CreateCopy();
                    parentRec.MarkFilesDirty();
                    ParsekLog.Info("Flight",
                        $"CreateSplitBranch: copied VesselSnapshot to parent '{parentRecordingId}' " +
                        "from CaptureAtStop (always-tree root had no snapshot)");
                }

                // T59: Copy rewind save from split recorder's CaptureAtStop to the root
                // recording. BuildCaptureRecording (FlightRecorder line 4562) copies the
                // filename into CaptureAtStop then clears it on the recorder (line 4573).
                // After the split, the new child recorder never has the rewind save, so
                // FinalizeTreeRecordings / StashActiveTreeAsPendingLimbo can't find it.
                // Copying here preserves it for the R button and rewind budget.
                ParsekFlight.CopyRewindSaveToRoot(host.activeTree, splitRecorder);
            }

            // Set ChildBranchPointId on parent
            Recording parentRecording;
            if (host.activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording))
            {
                // Will be set after we know the BP id
            }

            // Take snapshots for both child vessels
            ConfigNode activeSnapshot = FlightRecorder.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = FlightRecorder.TryBackupSnapshot(backgroundVessel);

            // BOTH ROUTE CONSUMERS BELOW ARE UNDOCK-ONLY, AND THAT IS THE CONTRACT.
            // A joint break, a stack decoupler or a radial decoupler also reaches
            // CreateSplitBranch, with a DIFFERENT branchType, and neither completes a route
            // window nor binds a start-docked origin: the pair stays PENDING and the proof
            // stays a non-origin. Deliberate, not an oversight - the note under design
            // 19.2.2's provenance taxonomy says a tanker that DECOUPLES from a base is
            // witnessable "in principle" but is explicitly out of scope for every milestone,
            // and the revisit path it names is a separate "separated-with" loading variant
            // anchored to the controlled-decouple event, NOT a widening of this gate.
            if (branchType == BranchPointType.Undock && parentRec != null)
            {
                bool routeWindowCompleted = RouteProofCapture.TryCompleteLatestRouteConnectionWindow(
                    parentRec,
                    branchUT,
                    activeSnapshot,
                    bgSnapshot);
                if (routeWindowCompleted)
                {
                    ParsekLog.Info("Flight",
                        $"Route proof dock window completed on undock: parent={parentRecordingId} " +
                        $"ut={branchUT.ToString("R", CultureInfo.InvariantCulture)}");
                }

                // THE START-DOCKED ORIGIN BIND (P12). The recording started on a settled
                // docked pair; capture recorded BOTH halves and deferred the origin choice to
                // here, where the split has actually happened. The TRANSPORT is the half the
                // RUN was flying - named by the connection window that witnessed the dock, or
                // by which half's cargo rose across the docked span, with the focused side as
                // the last resort - and the origin is the other half. Ordering matters: the
                // window above is completed FIRST, so the binder can read it.
                //
                // THE PROOF WAS ADOPTED ONTO parentRec above by AppendCapturedDataToRecording
                // (write-once), so this binds the copy that will actually be persisted.
                TryBindStartDockedOriginOnUndock(
                    parentRec, parentRecordingId, activeVessel, backgroundVessel,
                    activeSnapshot, bgSnapshot, branchUT);
            }

            // Look up the parent's Generation so the children inherit correctly.
            // First-split path: parentRecording is the freshly-wrapped rootRec at gen=0.
            // Subsequent-split path: parent is whatever the active recording's gen is.
            int parentGen = parentRecording != null ? parentRecording.Generation : 0;

            // Build the branch data
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId, host.activeTree.Id, branchUT, branchType,
                activeVessel != null ? activeVessel.persistentId : 0,
                activeVessel != null ? Recording.ResolveLocalizedName(activeVessel.vesselName) : "Unknown",
                backgroundVessel != null ? backgroundVessel.persistentId : 0,
                backgroundVessel != null ? Recording.ResolveLocalizedName(backgroundVessel.vesselName) : "Unknown",
                evaCrewName, evaVesselPid,
                parentGeneration: parentGen);

            // Set snapshots on child recordings
            activeChild.GhostVisualSnapshot = activeSnapshot;
            activeChild.VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null;
            bgChild.GhostVisualSnapshot = bgSnapshot;
            bgChild.VesselSnapshot = bgSnapshot != null ? bgSnapshot.CreateCopy() : null;

            // Pin the start-of-recording controller identity for each child from the
            // live split-moment vessel. Pure-factory boundary kept clean by capturing
            // here rather than threading Vessel into BuildSplitBranchData.
            activeChild.Controllers = ControllerInfo.CaptureFromVessel(activeVessel);
            bgChild.Controllers = ControllerInfo.CaptureFromVessel(backgroundVessel);

            // Same boundary, same reason, for the launch-unique identity: EACH child is
            // stamped from ITS OWN live vessel (never the other half's, and never the
            // parent's), falling back to that child's own snapshot `pid` - the value the
            // load-time backfill would read one save later. The active child would also pick
            // one up from the recorder-start backstop; the background child has no such path,
            // and a guid-less segment chained to a stamped one is the MIXED survivor set the
            // recovery correlator cannot corroborate
            // (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2).
            activeChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(activeVessel),
                VesselLaunchIdentity.TryReadVesselGuid(activeChild.VesselSnapshot));
            bgChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(backgroundVessel),
                VesselLaunchIdentity.TryReadVesselGuid(bgChild.VesselSnapshot));

            // Set ChildBranchPointId on parent recording
            if (parentRecording != null)
                parentRecording.ChildBranchPointId = bp.Id;

            // Add to tree
            host.activeTree.BranchPoints.Add(bp);
            host.activeTree.AddOrReplaceRecording(activeChild);
            host.activeTree.AddOrReplaceRecording(bgChild);

            // Set active recording
            host.activeTree.ActiveRecordingId = activeChild.RecordingId;

            // Add background child to BackgroundMap FIRST, then notify BackgroundRecorder
            if (backgroundVessel != null && backgroundVessel.persistentId != 0)
            {
                var initialBackgroundEnvOverride = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                    branchType,
                    backgroundVessel.isEVA,
                    activeVessel != null ? (int)activeVessel.situation : 0,
                    backgroundVessel.srfSpeed);

                if (initialBackgroundEnvOverride.HasValue)
                {
                    ParsekLog.Verbose("Flight",
                        $"CreateSplitBranch: forcing initial background env {initialBackgroundEnvOverride.Value} " +
                        $"for pid={backgroundVessel.persistentId}");
                }

                host.activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
                host.backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride,
                    initialTrajectoryPoint: backgroundInitialTrajectoryPoint);
            }

            // Create new FlightRecorder for active child
            host.recorder = new FlightRecorder();
            host.recorder.ActiveTree = host.activeTree;
            host.recorder.StartRecording(isPromotion: true);

            if (!host.recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"CreateSplitBranch: StartRecording failed for active child");
                host.recorder = null;
            }
            else
            {
                host.PrepareSessionStateForRecorderStart("CreateSplitBranch");
            }

            ParsekLog.Info("Flight", $"Tree branch created: type={branchType}, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId})" +
                (backgroundInitialTrajectoryPoint.HasValue ? ", bgInitialSeed=part-origin" : "") +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranch:exit", host.CaptureRecorderState());

            // Phase 4 (Rewind-to-Staging): Undock + EVA paths call CreateSplitBranch
            // directly. Both are multi-controllable when both outputs carry command
            // authority (EVA kerbals and probe-cored vessels pass IsTrackableVessel).
            // JointBreak flows through ProcessBreakupEvent; its RP hook lives there.
            if (branchType == BranchPointType.Undock || branchType == BranchPointType.EVA)
            {
                TryAuthorRewindPointForSplit(bp, branchType, activeVessel, activeChild, backgroundVessel, bgChild);
            }
        }

        /// <summary>
        /// Live half of the start-docked origin BIND (P12). Reduces the two post-split
        /// snapshots to part-pid sets, reads the background vessel's live pid + launch guid
        /// and the recorded launch's, and hands the whole decision to
        /// <see cref="RouteProofCapture.TryBindStartDockedOriginAtUndock"/> - which owns the
        /// binding rule, the guid gate, the manifest re-scoping and the pickup validation, and
        /// is therefore drivable headlessly.
        ///
        /// <para>WHICH HALF IS WHICH is NOT decided here and no longer follows focus. The
        /// recorder does follow the FOCUSED vessel across a split (see
        /// <see cref="DeferredUndockBranch"/>), but focus is only a proxy for "which half the
        /// run is flying" and it is wrong whenever the merge made the PARTNER dominant - the
        /// operator's 2026-09-03 relay, where rover C took fuel off rover B and KSP held focus
        /// on B, so the binder named C its own origin. This method now supplies BOTH sides'
        /// snapshots, part sets and live identities plus the recording's connection windows,
        /// and <see cref="RouteProofCapture.ResolveTransportHalfAtUndock"/> picks the transport
        /// from the run's ACTIONS with focus as the last resort.</para>
        /// </summary>
        private void TryBindStartDockedOriginOnUndock(
            Recording parentRec,
            string parentRecordingId,
            Vessel activeVessel,
            Vessel backgroundVessel,
            ConfigNode activeSnapshot,
            ConfigNode bgSnapshot,
            double branchUT)
        {
            RouteOriginProof proof = parentRec?.RouteOriginProof;
            if (proof == null)
                return;

            List<uint> activePids = VesselSnapshotOps.CollectPartPersistentIds(activeSnapshot);
            List<uint> backgroundPids = VesselSnapshotOps.CollectPartPersistentIds(bgSnapshot);

            uint backgroundLivePid = backgroundVessel != null ? backgroundVessel.persistentId : 0u;
            string backgroundLiveGuid = (backgroundVessel != null && backgroundVessel.id != Guid.Empty)
                ? backgroundVessel.id.ToString("N")
                : null;
            uint activeLivePid = activeVessel != null ? activeVessel.persistentId : 0u;
            string activeLiveGuid = (activeVessel != null && activeVessel.id != Guid.Empty)
                ? activeVessel.id.ToString("N")
                : null;

            // THE PREVIOUS RECORDING OF THIS VESSEL
            // (ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING). This recording opened
            // already docked, so it cannot have witnessed the load that put the cargo aboard
            // - but the recording BEFORE it can, and its connection windows are persisted.
            // The traversal is live because only here are the tree and its branch points in
            // hand; the DECISION is the pure ClassifyPredecessorPickup, which sees only the
            // window list. The walk is guid-gated and never leaves activeTree; see
            // docs/dev/research/pickup-predating-the-recording.md.
            Recording predecessorRec;
            string predecessorReason;
            bool havePredecessor = RouteProofCapture.TryResolveOriginProofPredecessor(
                host.activeTree, parentRec, out predecessorRec, out predecessorReason);
            ParsekLog.Verbose("Flight",
                $"RouteOriginProof predecessor walk: recording={parentRecordingId ?? "<none>"} " +
                $"resolved={(havePredecessor ? "1" : "0")} reason={predecessorReason} " +
                $"predecessor={(havePredecessor ? predecessorRec.RecordingId : "<none>")} " +
                $"predecessorWindows={(havePredecessor ? (predecessorRec.RouteConnectionWindows?.Count ?? 0) : 0)}");

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof,
                activePids,
                backgroundPids,
                activeSnapshot,
                parentRec.VesselSnapshot,
                backgroundLivePid,
                backgroundLiveGuid,
                parentRec.VesselPersistentId,
                parentRec.RecordedVesselGuid,
                branchUT,
                parentRecordingId,
                backgroundSideSnapshot: bgSnapshot,
                recordingConnectionWindows: parentRec.RouteConnectionWindows,
                activeSideLiveVesselPid: activeLivePid,
                activeSideLiveVesselGuid: activeLiveGuid,
                predecessorConnectionWindows: havePredecessor ? predecessorRec.RouteConnectionWindows : null,
                recordingStartUT: parentRec.StartUT,
                predecessorContext: havePredecessor ? predecessorRec.RecordingId : null);

            if (bound)
                parentRec.MarkFilesDirty();

            ParsekLog.Verbose("Flight",
                $"RouteOriginProof bind attempt: parent={parentRecordingId ?? "<none>"} " +
                $"bound={(bound ? "1" : "0")} bindState={proof.StartDockedOriginBindState} " +
                $"activeVessel='{activeVessel?.vesselName ?? "<none>"}' " +
                $"backgroundVessel='{backgroundVessel?.vesselName ?? "<none>"}' " +
                $"activeParts={activePids?.Count ?? 0} backgroundParts={backgroundPids?.Count ?? 0}");
        }

        internal void CreateSplitBranchFromBackgroundParent(
            string parentRecordingId,
            uint sourceVesselPid,
            Vessel activeVessel,
            Vessel backgroundVessel,
            double branchUT,
            string evaCrewName,
            uint evaVesselPid,
            string branchPath)
        {
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:entry", host.CaptureRecorderState());

            if (host.activeTree == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranchFromBackgroundParent: no active tree - aborting");
                return;
            }

            if (string.IsNullOrEmpty(parentRecordingId))
            {
                ParsekLog.Warn("Flight", "CreateSplitBranchFromBackgroundParent: no parent recording id - aborting");
                return;
            }

            Recording parentRecording;
            if (!host.activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording) || parentRecording == null)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' not found - aborting");
                return;
            }

            if (ParsekFlight.BackgroundParentAlreadySplit(parentRecording, host.activeTree))
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' " +
                    $"already has child branch '{parentRecording.ChildBranchPointId}' - aborting");
                return;
            }
            if (!string.IsNullOrEmpty(parentRecording.ChildBranchPointId))
            {
                ParsekLog.Verbose("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' " +
                    $"carries child branch '{parentRecording.ChildBranchPointId}' with no same-PID child " +
                    "(breakup-continuous) - splitting it; the new branch point takes the link");
            }

            string mappedParentRecordingId = null;
            if (sourceVesselPid == 0
                || !host.activeTree.BackgroundMap.TryGetValue(sourceVesselPid, out mappedParentRecordingId)
                || !string.Equals(mappedParentRecordingId, parentRecordingId, StringComparison.Ordinal))
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: source background map entry changed before branch " +
                    $"(sourcePid={sourceVesselPid}, expectedParent={parentRecordingId}, " +
                    $"mappedParent={mappedParentRecordingId ?? "null"}) - aborting");
                return;
            }

            if (activeVessel == null || backgroundVessel == null)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: child vessel missing " +
                    $"(active={(activeVessel != null ? activeVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}, " +
                    $"background={(backgroundVessel != null ? backgroundVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}) - aborting");
                return;
            }

            uint currentActivePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            if (currentActivePid == 0 || activeVessel.persistentId != currentActivePid)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: active child does not match FlightGlobals.ActiveVessel " +
                    $"(activeChildPid={activeVessel.persistentId}, flightActivePid={currentActivePid}) - aborting");
                return;
            }

            ConfigNode activeSnapshot = FlightRecorder.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = FlightRecorder.TryBackupSnapshot(backgroundVessel);
            int parentGen = parentRecording.Generation;

            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId, host.activeTree.Id, branchUT, BranchPointType.EVA,
                activeVessel.persistentId,
                Recording.ResolveLocalizedName(activeVessel.vesselName) ?? "Unknown",
                backgroundVessel.persistentId,
                Recording.ResolveLocalizedName(backgroundVessel.vesselName) ?? "Unknown",
                evaCrewName, evaVesselPid,
                parentGeneration: parentGen);

            activeChild.GhostVisualSnapshot = activeSnapshot;
            activeChild.VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null;
            bgChild.GhostVisualSnapshot = bgSnapshot;
            bgChild.VesselSnapshot = bgSnapshot != null ? bgSnapshot.CreateCopy() : null;

            // Pin the start-of-recording controller identity for each child from the
            // live split-moment vessel (background-parent EVA branch path).
            activeChild.Controllers = ControllerInfo.CaptureFromVessel(activeVessel);
            bgChild.Controllers = ControllerInfo.CaptureFromVessel(backgroundVessel);

            // Launch-unique identity, same contract as the sibling split path above: each
            // child from its OWN live vessel, snapshot `pid` as the fallback.
            activeChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(activeVessel),
                VesselLaunchIdentity.TryReadVesselGuid(activeChild.VesselSnapshot));
            bgChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(backgroundVessel),
                VesselLaunchIdentity.TryReadVesselGuid(bgChild.VesselSnapshot));

            string previousActiveRecordingId = host.activeTree.ActiveRecordingId;
            string previousParentChildBranchPointId = parentRecording.ChildBranchPointId;
            var previousBackgroundMapEntries = ParsekFlight.CaptureBackgroundMapEntries(
                host.activeTree,
                sourceVesselPid,
                activeVessel.persistentId,
                backgroundVessel.persistentId);

            parentRecording.ChildBranchPointId = bp.Id;
            host.activeTree.BranchPoints.Add(bp);
            host.activeTree.AddOrReplaceRecording(activeChild);
            host.activeTree.AddOrReplaceRecording(bgChild);
            host.activeTree.ActiveRecordingId = activeChild.RecordingId;

            // The source vessel is now either the active or background child, depending on KSP focus.
            host.activeTree.BackgroundMap.Remove(activeVessel.persistentId);
            host.activeTree.BackgroundMap.Remove(backgroundVessel.persistentId);
            SegmentEnvironment? initialBackgroundEnvOverride = null;
            if (backgroundVessel.persistentId != 0)
            {
                initialBackgroundEnvOverride = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                    BranchPointType.EVA,
                    backgroundVessel.isEVA,
                    (int)activeVessel.situation,
                    backgroundVessel.srfSpeed);

                if (initialBackgroundEnvOverride.HasValue)
                {
                    ParsekLog.Verbose("Flight",
                        $"CreateSplitBranchFromBackgroundParent: forcing initial background env " +
                        $"{initialBackgroundEnvOverride.Value} for pid={backgroundVessel.persistentId}");
                }

                host.activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
            }

            host.recorder = new FlightRecorder();
            host.recorder.ActiveTree = host.activeTree;
            host.recorder.StartRecording(isPromotion: true);

            if (!host.recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: StartRecording failed for active child " +
                    $"pid={activeChild.VesselPersistentId}");
                host.recorder = null;
                ParsekFlight.RollbackBackgroundParentBranchMutation(
                    host.activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            if (host.recorder.RecordingVesselId != activeChild.VesselPersistentId)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: recorder vessel mismatch after start " +
                    $"(recorderPid={host.recorder.RecordingVesselId}, activeChildPid={activeChild.VesselPersistentId})");
                host.recorder.StopRecordingForChainBoundary();
                host.recorder = null;
                ParsekFlight.RollbackBackgroundParentBranchMutation(
                    host.activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            host.backgroundRecorder?.OnVesselRemovedFromBackground(sourceVesselPid);
            if (backgroundVessel.persistentId != 0)
            {
                host.backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride);
            }

            host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
            host.PrepareSessionStateForRecorderStart("CreateSplitBranchFromBackgroundParent");

            ParsekLog.Info("Flight", $"Tree branch created: type=EVA, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId}), " +
                $"path={branchPath}" +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:exit", host.CaptureRecorderState());

            TryAuthorRewindPointForSplit(bp, BranchPointType.EVA, activeVessel, activeChild, backgroundVessel, bgChild);
        }

        /// <summary>
        /// Phase 4 (Rewind-to-Staging, design §5.1 + §7.1 + §7.2 + §7.19): if the
        /// split has >=2 controllable children, builds a <see cref="ChildSlot"/>
        /// list from the freshly-created child recordings and hands it to
        /// <see cref="RewindPointAuthor.Begin"/>. Called from both the Undock/EVA
        /// side of <see cref="CreateSplitBranch"/> and from the structural
        /// <c>ProcessBreakupEvent</c> path. An EVA split reaches Begin and is
        /// refused there (<see cref="RewindPointAuthor.IsReFlySplitType"/>): Re-Fly
        /// is for vessel separations only.
        ///
        /// <para>
        /// The resolver returned here looks up the child PID by recording id in
        /// <c>activeTree.Recordings</c>, because at split time the newly-created
        /// child recordings are in the active tree but not yet committed.
        /// </para>
        /// </summary>
        void TryAuthorRewindPointForSplit(
            BranchPoint bp,
            BranchPointType branchType,
            Vessel activeVessel, Recording activeChild,
            Vessel backgroundVessel, Recording backgroundChild)
        {
            if (bp == null)
            {
                ParsekLog.Warn("Rewind", $"TryAuthorRewindPointForSplit: bp is null ({branchType})");
                return;
            }

            var candidatePids = new List<uint>();
            if (activeVessel != null) candidatePids.Add(activeVessel.persistentId);
            if (backgroundVessel != null) candidatePids.Add(backgroundVessel.persistentId);

            uint origPid = activeVessel != null ? activeVessel.persistentId : 0;
            var controllable = SegmentBoundaryLogic.IdentifyControllableChildren(origPid, candidatePids);

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Single-controllable split: no RP (bp={bp.Id} type={branchType} " +
                    $"controllable={controllable.Count})");
                return;
            }

            AuthorRewindPointFromVesselRecordings(
                bp,
                controllable,
                new (Vessel vessel, Recording rec)[] {
                    (activeVessel, activeChild),
                    (backgroundVessel, backgroundChild)
                });
        }

        /// <summary>
        /// Phase 4: shared back-end for both Undock/EVA and Breakup RP authoring.
        /// Builds <see cref="ChildSlot"/> entries for each controllable (vessel, recording)
        /// pair whose PID is present in <paramref name="controllablePids"/>, then
        /// delegates to <see cref="RewindPointAuthor.Begin"/>. The resolver returned
        /// here maps <c>OriginChildRecordingId</c> -> <c>Vessel.persistentId</c> using
        /// the just-built map so the deferred coroutine does not need to consult the
        /// active tree or <c>RecordingStore.CommittedRecordings</c>.
        /// </summary>
        void AuthorRewindPointFromVesselRecordings(
            BranchPoint bp,
            List<uint> controllablePids,
            IReadOnlyList<(Vessel vessel, Recording rec)> pairs)
        {
            var slots = new List<ChildSlot>();
            var recIdToPid = new Dictionary<string, uint>(StringComparer.Ordinal);

            int slotIndex = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var (vessel, rec) = pairs[i];
                if (vessel == null || rec == null) continue;
                if (!controllablePids.Contains(vessel.persistentId)) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                slots.Add(new ChildSlot
                {
                    SlotIndex = slotIndex++,
                    OriginChildRecordingId = rec.RecordingId,
                    Controllable = true,
                    Disabled = false,
                    DisabledReason = null
                });
                recIdToPid[rec.RecordingId] = vessel.persistentId;
            }

            if (slots.Count < 2)
            {
                ParsekLog.Info("Rewind",
                    $"Multi-controllable classifier passed ({controllablePids.Count}) but slot build " +
                    $"produced only {slots.Count} entries (bp={bp.Id}); skipping RP");
                return;
            }

            var ctx = new RewindPointAuthorContext
            {
                RecordingResolver = recId =>
                    recIdToPid.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null
            };

            RewindPointAuthor.Begin(bp, slots, controllablePids, ctx);
        }

        /// <summary>
        /// Phase 4: RP hook for structural joint-break (Breakup) branch points.
        /// Treats the surviving parent + each controllable child fragment as
        /// candidates; if at least 2 pass <see cref="SegmentBoundaryLogic.IdentifyControllableChildren"/>,
        /// delegates to <see cref="AuthorRewindPointFromVesselRecordings"/>.
        /// </summary>
        void TryAuthorRewindPointForBreakup(
            BranchPoint breakupBp,
            IReadOnlyList<(Vessel vessel, Recording rec)> pairs)
        {
            if (breakupBp == null || pairs == null || pairs.Count == 0)
                return;

            var candidatePids = new List<uint>();
            for (int i = 0; i < pairs.Count; i++)
            {
                var v = pairs[i].vessel;
                if (v != null) candidatePids.Add(v.persistentId);
            }

            uint origPid = pairs.Count > 0 && pairs[0].vessel != null ? pairs[0].vessel.persistentId : 0;
            var controllable = SegmentBoundaryLogic.IdentifyControllableChildren(origPid, candidatePids);

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Single-controllable split: no RP (bp={breakupBp.Id} type={breakupBp.Type} " +
                    $"controllable={controllable.Count})");
                return;
            }

            AuthorRewindPointFromVesselRecordings(breakupBp, controllable, pairs);
        }

        /// <summary>
        /// Returns whichever of <c>data.from.vessel</c> / <c>data.to.vessel</c> is the
        /// recorder's OWN (transport) vessel, matched by <paramref name="recordingVesselId"/>,
        /// INCLUDING when that side is the dock survivor (<c>data.to.vessel</c>, whose pid
        /// becomes the merged pid). Used to capture a pre-couple transport snapshot for the
        /// route window's DOCK_TRANSPORT_RESOURCES baseline. Returns null when neither side
        /// matches (the recorder is observing a third vessel), in which case the caller
        /// falls back to the post-couple merged snapshot (current behaviour). Unlike a dock
        /// PARTNER, the transport often IS the survivor, so this must NOT pick the
        /// non-survivor side the way <see cref="ResolveDockPartnerPidFromEvent"/> does.
        /// </summary>
        private static Vessel ResolveSelfVessel(
            GameEvents.FromToAction<Part, Part> data, uint recordingVesselId)
        {
            if (recordingVesselId == 0) return null;
            if (data.from?.vessel != null && data.from.vessel.persistentId == recordingVesselId)
                return data.from.vessel;
            if (data.to?.vessel != null && data.to.vessel.persistentId == recordingVesselId)
                return data.to.vessel;
            return null;
        }

        /// <summary>
        /// Cross-tree partner fallback for the dock-route window's
        /// <c>EndpointPartPersistentIds</c>. Tries (1) a live KSP vessel snapshot,
        /// (2) any matching recording in <c>activeTree.Recordings</c>, (3) any
        /// matching recording in <see cref="RecordingStore.CommittedRecordings"/>.
        /// Returns null when no usable snapshot can be located; callers preserve any
        /// existing list rather than overwriting it with null.
        /// </summary>
        internal List<uint> TryDeriveEndpointPartPidsFromPartner(uint partnerPid)
        {
            if (partnerPid == 0) return null;

            Vessel partnerVessel = FlightRecorder.FindVesselByPid(partnerPid);
            if (partnerVessel != null)
            {
                ConfigNode partnerSnapshot = FlightRecorder.TryBackupSnapshot(partnerVessel);
                if (partnerSnapshot != null)
                {
                    List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(partnerSnapshot);
                    if (pids != null && pids.Count > 0)
                    {
                        ParsekLog.Verbose("Flight",
                            $"TryDeriveEndpointPartPidsFromPartner: live-vessel snapshot " +
                            $"partnerPid={partnerPid} parts={pids.Count}");
                        return pids;
                    }
                }
            }

            if (host.activeTree?.Recordings != null)
            {
                foreach (var kvp in host.activeTree.Recordings)
                {
                    Recording r = kvp.Value;
                    if (r != null && r.VesselPersistentId == partnerPid && r.VesselSnapshot != null)
                    {
                        List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(r.VesselSnapshot);
                        if (pids != null && pids.Count > 0)
                        {
                            ParsekLog.Verbose("Flight",
                                $"TryDeriveEndpointPartPidsFromPartner: active-tree recording " +
                                $"partnerPid={partnerPid} recId={r.RecordingId} parts={pids.Count}");
                            return pids;
                        }
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording r = committed[i];
                    if (r != null && r.VesselPersistentId == partnerPid && r.VesselSnapshot != null)
                    {
                        List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(r.VesselSnapshot);
                        if (pids != null && pids.Count > 0)
                        {
                            ParsekLog.Verbose("Flight",
                                $"TryDeriveEndpointPartPidsFromPartner: committed recording " +
                                $"partnerPid={partnerPid} recId={r.RecordingId} parts={pids.Count}");
                            return pids;
                        }
                    }
                }
            }

            ParsekLog.Verbose("Flight",
                $"TryDeriveEndpointPartPidsFromPartner: no snapshot available partnerPid={partnerPid}");
            return null;
        }

        /// <summary>
        /// Creates a tree merge branch point for a dock or board event.
        /// Ends parent recordings, creates child recording for the merged vessel,
        /// and starts a new FlightRecorder for the child.
        /// Per Orchestrator Fix 3: absorbedVesselPid is NOT a parent-selection
        /// parameter -- background vessel PID is derived from the background parent
        /// recording. routeTargetVesselPid is only the route-proof endpoint PID.
        /// </summary>
        internal void CreateMergeBranch(
            BranchPointType branchType,
            uint mergedVesselPid,
            string activeParentRecordingId,      // from the active recorder (always present)
            string backgroundParentRecordingId,  // from BackgroundMap lookup (null for foreign vessel)
            double mergeUT,
            FlightRecorder stoppedRecorder,
            uint routeTargetVesselPid = 0,
            RouteConnectionKind transferKind = RouteConnectionKind.None,
            uint branchPartnerPid = 0,
            string branchPartnerGuid = null)
        {
            // 1. Build parent recording ID list
            var parentIds = new List<string>();
            parentIds.Add(activeParentRecordingId);
            if (backgroundParentRecordingId != null)
                parentIds.Add(backgroundParentRecordingId);

            // 2. Flush captured data from stopped recorder into the active parent recording
            Recording activeParentRec = ParsekFlight.FlushStoppedRecorderIntoMergeParent(
                host.activeTree, activeParentRecordingId, stoppedRecorder, branchType, mergeUT);

            // 3. End background parent recording (if two-parent merge)
            // Per Fix 3: derive background vessel PID from the recording's VesselPersistentId
            Recording bgParentRec = null;
            if (backgroundParentRecordingId != null)
            {
                if (host.activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
                {
                    bgParentRec.ExplicitEndUT = mergeUT;
                    bgParentRec.StampTerminalState(
                        (branchType == BranchPointType.Dock)
                            ? TerminalState.Docked : TerminalState.Boarded,
                        "MergeBranch.backgroundParent");

                    // Remove background vessel from BackgroundMap and notify BackgroundRecorder
                    uint bgVesselPid = bgParentRec.VesselPersistentId;
                    host.backgroundRecorder?.OnVesselRemovedFromBackground(bgVesselPid);
                    host.activeTree.BackgroundMap.Remove(bgVesselPid);
                }
            }

            // 4. Get merged vessel name
            Vessel mergedVessel = FlightRecorder.FindVesselByPid(mergedVesselPid);
            string mergedVesselName = Recording.ResolveLocalizedName(mergedVessel?.vesselName) ?? "Merged Vessel";

            // 5. Build merge branch data
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentIds, host.activeTree.Id, mergeUT, branchType,
                mergedVesselPid, mergedVesselName,
                routeTargetVesselPid,
                routeTargetVesselPid != 0 ? transferKind : RouteConnectionKind.None,
                branchPartnerPid,
                branchPartnerGuid);

            // 6. Take snapshot of merged vessel
            ConfigNode mergedSnapshot = (mergedVessel != null)
                ? FlightRecorder.TryBackupSnapshot(mergedVessel) : null;
            mergedChild.GhostVisualSnapshot = mergedSnapshot;
            mergedChild.VesselSnapshot = mergedSnapshot != null ? mergedSnapshot.CreateCopy() : null;

            if (branchType == BranchPointType.Dock && routeTargetVesselPid != 0)
            {
                ConfigNode transportSnapshot =
                    stoppedRecorder?.CaptureAtStop?.VesselSnapshot ?? activeParentRec?.VesselSnapshot;
                List<uint> transportPartPids = VesselSnapshotOps.CollectPartPersistentIds(transportSnapshot);

                // Prefer the pre-couple partner snapshot captured in OnPartCouple
                // (before KSP reparented data.to.vessel to include transport parts).
                // It carries the partner's pre-dock part PID set and pre-dock
                // resources/inventory, so DOCK_ENDPOINT_RESOURCES isn't inflated by
                // the transport's tank.
                ConfigNode endpointPreCoupleSnapshot = null;
                if (host.pendingDockPartnerSnapshot != null
                    && host.pendingDockPartnerSnapshotPid == routeTargetVesselPid)
                {
                    endpointPreCoupleSnapshot = host.pendingDockPartnerSnapshot;
                }

                // Pre-couple transport baseline captured in OnPartCouple (before the
                // same-frame stock crossfeed equalisation that the deferred merged
                // snapshot would otherwise bake in, deflating DOCK_TRANSPORT_RESOURCES and
                // tripping the MixedPickupDelivery gate). BuildDockRouteConnectionWindow
                // self-validates it against the transport PID set and falls back to the
                // merged snapshot when absent / mismatched, so this is unconditional.
                ConfigNode transportPreCoupleSnapshot = host.pendingDockSelfSnapshot;

                List<uint> endpointPartPids = null;
                if (endpointPreCoupleSnapshot != null)
                {
                    endpointPartPids = VesselSnapshotOps.CollectPartPersistentIds(endpointPreCoupleSnapshot);
                }
                if ((endpointPartPids == null || endpointPartPids.Count == 0)
                    && bgParentRec?.VesselSnapshot != null)
                {
                    endpointPartPids = VesselSnapshotOps.CollectPartPersistentIds(bgParentRec.VesselSnapshot);
                }

                // Cross-tree partner fallback: when the partner has a committed
                // recording from a prior tree (or is otherwise not in
                // activeTree.BackgroundMap), bgParentRec is null and the pre-couple
                // snapshot was not captured (e.g. retroactive merge path). Derive
                // part PIDs from any known recording for the partner PID.
                if ((endpointPartPids == null || endpointPartPids.Count == 0)
                    && routeTargetVesselPid != 0)
                {
                    endpointPartPids = TryDeriveEndpointPartPidsFromPartner(routeTargetVesselPid)
                        ?? endpointPartPids;
                }

                RouteEndpoint? endpointAtDock = host.pendingDockRouteEndpointAtDock;
                int endpointSituation = host.pendingDockRouteEndpointSituation;

                if ((!endpointAtDock.HasValue || endpointSituation < 0) &&
                    bgParentRec?.VesselSnapshot != null &&
                    bgParentRec.VesselPersistentId == routeTargetVesselPid &&
                    ParsekFlight.TryBuildRouteEndpointFromSnapshot(
                        bgParentRec.VesselSnapshot,
                        routeTargetVesselPid,
                        out RouteEndpoint snapshotEndpoint,
                        out int snapshotSituation))
                {
                    endpointAtDock = snapshotEndpoint;
                    endpointSituation = snapshotSituation;
                    ParsekLog.Verbose("Flight",
                        $"Route proof endpoint restored from background snapshot targetPid={routeTargetVesselPid} " +
                        $"situation={endpointSituation}");
                }

                if (!endpointAtDock.HasValue || endpointSituation < 0)
                {
                    Vessel endpointVessel = FlightRecorder.FindVesselByPid(routeTargetVesselPid);
                    endpointAtDock = BuildRouteEndpointFromVessel(
                        endpointVessel,
                        routeTargetVesselPid);
                    endpointSituation = endpointVessel != null ? (int)endpointVessel.situation : -1;
                }

                if (!endpointAtDock.HasValue || endpointSituation < 0)
                {
                    ParsekLog.Verbose("Flight",
                        $"Route proof endpoint unavailable at dock targetPid={routeTargetVesselPid}; " +
                        "route analysis will reject this candidate");
                }

                // THE ENDPOINT'S LAUNCH-UNIQUE IDENTITY, read while it is still readable.
                // Preferred source is the PRE-COUPLE partner snapshot: after Part.Couple the
                // endpoint's own Vessel is destroyed and every id left on the merged craft is
                // craft-baked, so a root flightID taken later would be the TRANSPORT's. The
                // background parent's snapshot is the same shape and serves as the fallback;
                // an unreadable root leaves 0, which degrades the predecessor-window partner
                // match to the part-pid overlap rather than refusing it.
                uint endpointRootPartUId = 0u;
                if (endpointPreCoupleSnapshot == null
                    || !VesselSnapshotOps.TryReadRootPartFlightId(endpointPreCoupleSnapshot, out endpointRootPartUId))
                {
                    if (bgParentRec?.VesselSnapshot == null
                        || bgParentRec.VesselPersistentId != routeTargetVesselPid
                        || !VesselSnapshotOps.TryReadRootPartFlightId(bgParentRec.VesselSnapshot, out endpointRootPartUId))
                    {
                        endpointRootPartUId = 0u;
                    }
                }

                // BACKFILL THE ENDPOINT DESCRIPTOR from the same reading, when the descriptor
                // itself could not supply one. The window's endpoint IS what a delivery stop
                // persists verbatim (RouteBuilder), and its root id is what keeps the stop
                // resolving by identity while a visitor is docked to the destination
                // (ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER). The two readings have the
                // same source and the same pre-couple timing, so this cannot disagree with the
                // descriptor - it only fills a gap the descriptor's own path left (a live
                // vessel with no instantiated rootPart, or a snapshot with no readable root).
                //
                // THE POST-COUPLE FALLBACK ABOVE IS SAFE AND NOT THE REASON THIS EXISTS,
                // re-derived rather than assumed: it calls FindVesselByPid on the ENDPOINT's
                // pid AFTER the couple, so it resolves at all only when the endpoint half was
                // DOMINANT and survived as the merged vessel - in which case the merged
                // vessel's root part IS the endpoint's own. An absorbed endpoint's pid is gone
                // from FlightGlobals and that path yields no endpoint at all.
                if (endpointAtDock.HasValue
                    && endpointAtDock.Value.RootPartUId == 0u
                    && endpointRootPartUId != 0u)
                {
                    RouteEndpoint backfilled = endpointAtDock.Value;
                    backfilled.RootPartUId = endpointRootPartUId;
                    endpointAtDock = backfilled;
                    ParsekLog.Verbose("Flight",
                        "Route proof endpoint root backfilled from the pre-couple snapshot: "
                        + "targetPid=" + routeTargetVesselPid.ToString(CultureInfo.InvariantCulture)
                        + " rootPartUId=" + endpointRootPartUId.ToString(CultureInfo.InvariantCulture));
                }

                RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                    mergeUT,
                    routeTargetVesselPid,
                    transferKind,
                    mergedSnapshot,
                    transportPartPids,
                    endpointPartPids,
                    endpointAtDock,
                    endpointSituation,
                    endpointPreCoupleSnapshot,
                    transportPreCoupleSnapshot,
                    endpointRootPartUId);

                if (window != null)
                {
                    mergedChild.RouteConnectionWindows = new List<RouteConnectionWindow> { window };
                    ParsekLog.Info("Flight",
                        $"Route proof dock window captured: child={mergedChild.RecordingId} " +
                        $"window={window.WindowId} targetPid={routeTargetVesselPid} " +
                        $"kind={window.TransferKind} " +
                        $"transportParts={window.TransportPartPersistentIds?.Count ?? 0} " +
                        $"endpointParts={window.EndpointPartPersistentIds?.Count ?? 0} " +
                        $"endpointRoot={window.EndpointRootPartUId.ToString(CultureInfo.InvariantCulture)}");
                }
                else
                {
                    ParsekLog.Warn("Flight",
                        $"Route proof dock window capture skipped: child={mergedChild.RecordingId} " +
                        $"targetPid={routeTargetVesselPid} snapshot={mergedSnapshot != null} " +
                        $"transportParts={transportPartPids?.Count ?? 0} " +
                        $"endpointParts={endpointPartPids?.Count ?? 0}");
                }
            }

            // 7. Set ChildBranchPointId on all parent recordings
            if (activeParentRec != null)
                activeParentRec.ChildBranchPointId = bp.Id;
            if (backgroundParentRecordingId != null)
            {
                Recording bgParentRec2;
                if (host.activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec2))
                    bgParentRec2.ChildBranchPointId = bp.Id;
            }

            // 8. Add to tree
            host.activeTree.BranchPoints.Add(bp);
            host.activeTree.AddOrReplaceRecording(mergedChild);

            // 8b. Phantom-rover fix: when this DOCK absorbed a Parsek-spawned / adopted
            // vessel that has its own committed terminal leaf (cross-tree, e.g. a landed
            // rover a logistics transport docked into), that leaf's live vessel just
            // disappeared into the merged vessel. Suppress its terminal spawn so the
            // spawn-death check + KSCSpawn don't later materialise a duplicate at the
            // runway.
            //
            // PHANTOM-SUPERSEDE-RIDES-GATED-PID: the absorbed pid is the branch point's
            // UNGATED couple-event partner stamp (bp.TargetVesselPersistentId), not the
            // route-eligibility-gated routeTargetVesselPid. Suppression is a partner-
            // identity question; a dock that absorbs a Parsek-recorded vessel but fails
            // route eligibility absorbs it just as physically. See
            // ResolveDockMergeSpawnSuppressionPid for the mechanism and the EVA carve-out.
            //
            // Marked at MERGE time (not commit time) and never reverted: the dock physically
            // merges the two vessels, which a later active-tree discard does NOT undo, so the
            // absorbed leaf must stay suppressed even if mergedChild is never committed (and
            // commit-time-only marking would re-expose the phantom on discard). A dangling
            // supersede id is harmless because ShouldSpawnAtRecordingEnd uses the field only as
            // a non-empty gate.
            uint spawnSuppressionPid = ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                branchType, bp.TargetVesselPersistentId, mergedVesselPid);
            if (spawnSuppressionPid != 0)
            {
                string absorbedLaunchGuid =
                    (host.pendingDockPartnerSnapshot != null
                     && host.pendingDockPartnerSnapshotPid == spawnSuppressionPid)
                        ? VesselLaunchIdentity.TryReadVesselGuid(host.pendingDockPartnerSnapshot)
                        : null;
                int superseded = TreeCommitService.MarkTerminalSpawnSupersededByDockMerge(
                    spawnSuppressionPid,
                    absorbedLaunchGuid,
                    mergedVesselPid,
                    mergedChild.RecordingId,
                    "CreateMergeBranch");
                ParsekLog.Info("Flight",
                    $"Dock-merge superseded {superseded} committed terminal spawn(s): " +
                    $"absorbedPid={spawnSuppressionPid} routeTargetPid={routeTargetVesselPid} " +
                    $"mergedPid={mergedVesselPid} continuation={mergedChild.RecordingId}");
            }

            // 9. Set active recording
            host.activeTree.ActiveRecordingId = mergedChild.RecordingId;

            // 10. Start new FlightRecorder for merged child
            host.recorder = new FlightRecorder();
            host.recorder.ActiveTree = host.activeTree;
            host.recorder.StartRecording(isPromotion: true);

            if (!host.recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", "CreateMergeBranch: StartRecording failed for merged child");
                host.recorder = null;
            }
            else
            {
                host.PrepareSessionStateForRecorderStart("CreateMergeBranch");
            }

            // 11. Log
            ParsekLog.Info("Flight", $"Tree merge created: type={branchType}, " +
                $"bp={bp.Id}, parents=[{string.Join(",", parentIds)}], " +
                $"child={mergedChild.RecordingId} (pid={mergedVesselPid})");
        }

        /// <summary>
        /// The name of a standalone fallback leg (<see cref="FallbackCommitSplitRecorder"/>):
        /// the recorded vessel's live name on a positive pid + guid match, else the name the
        /// recorded vessel had when this leg started (the start-of-leg ghost snapshot), else
        /// "Unknown Vessel". Logs the source.
        /// </summary>
        internal static string ResolveStandaloneFallbackLegName(FlightRecorder splitRec, Recording captured)
        {
            uint recordedPid = splitRec != null ? splitRec.RecordingVesselId : 0u;
            string startName = Recording.ResolveLocalizedName(
                captured?.GhostVisualSnapshot?.GetValue("name"));
            Vessel recordedVessel = recordedPid != 0 ? FlightRecorder.FindVesselByPid(recordedPid) : null;
            string name = ParsekFlight.ResolveRecordedVesselNameAtClose(
                startName,
                recordedPid,
                captured?.RecordedVesselGuid,
                recordedVessel != null ? recordedVessel.persistentId : 0u,
                AnchorDetector.TryReadLiveVesselGuid(recordedVessel),
                recordedVessel != null ? Recording.ResolveLocalizedName(recordedVessel.vesselName) : null,
                out string source);
            if (string.IsNullOrEmpty(name))
            {
                name = "Unknown Vessel";
                source = "unknown";
            }
            ParsekLog.Verbose("Flight",
                $"FallbackCommitSplitRecorder: leg name '{name}' source={source} pid={recordedPid} " +
                $"(stop-time active-vessel name '{captured?.VesselName ?? "(null)"}' is not used)");
            return name;
        }

        /// <summary>
        /// Fallback: if tree append fails, commit captured data directly.
        /// In always-tree mode the tree path should succeed for the active vessel;
        /// this fallback handles edge cases (no active tree, debris).
        /// </summary>
        internal void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            RecorderStateLog.RecState("FallbackCommitSplitRecorder:entry", host.CaptureRecorderState());
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;
            ParsekFlight.ApplyPendingSplitDestructionToCapturedRecording(
                captured,
                splitRec.VesselDestroyedDuringRecording,
                "FallbackCommitSplitRecorder.entry");

            // Bug #297: when in tree mode, append to the active tree recording
            // instead of orphaning data as a standalone recording.
            if (ParsekFlight.TryAppendCapturedToTree(host.activeTree, captured))
                return;

            if (ParsekFlight.TryDiscardCaptureOfDroppedTree(
                    host.activeTree, splitRec.ActiveTree, captured, splitRec.RecordingVesselId))
                return;

            // The leg's name comes from the RECORDED vessel, never from the stop-time
            // capture's VesselName (the ACTIVE vessel's name at stop, which after a split
            // may already be the new pod).
            string legName = ResolveStandaloneFallbackLegName(splitRec, captured);
            var rec = RecordingStore.CreateRecordingFromFlightData(
                captured.Points, legName,
                orbitSegments: captured.OrbitSegments,
                partEvents: captured.PartEvents,
                flagEvents: captured.FlagEvents);

            if (rec == null)
            {
                ParsekLog.Warn("Flight", "Split branch failed — recording too short to commit, data lost");
                return;
            }

            // Copy snapshot/vessel state and location context to the recording.
            ParsekFlight.ApplyCapturedSplitStateToStandaloneRecording(rec, captured, splitRec.RecordingVesselId);

            // Tag segment phase if untagged
            ParsekFlight.TagSegmentPhaseIfMissing(rec, FlightGlobals.ActiveVessel);

            // Destroyed vessels: discard pad failures or idle-on-pad, otherwise commit directly.
            // Both discards require pad evidence on the recording itself (this branch handles
            // a single recording): a one-sample recording has duration 0 and maxDist 0, so the
            // thresholds alone would discard an orbiting recording destroyed within one
            // sample interval.
            if (rec.VesselDestroyed)
            {
                double dur = rec.EndUT - rec.StartUT;
                double maxDist = rec.MaxDistanceFromLaunch;
                string discardReason = ParsekFlight.DecideDestroyedSplitRecordingDiscard(rec);
                if (discardReason == "pad failure")
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — pad failure ({dur:F1}s, {maxDist:F0}m), discarding");
                    return;
                }
                if (discardReason == "idle on pad")
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — idle on pad (maxDist={maxDist:F1}m), discarding");
                    return;
                }
                if (discardReason == null)
                {
                    // Log WHY this destroyed recording commits, once, from values the
                    // decision already produced: HasPadEvidence is the decision's own
                    // gate (no thresholds recomputed here), so evidence with a null
                    // decision means the thresholds did not match.
                    bool hasPadEvidence = ParsekFlight.HasPadEvidence(rec);
                    string commitWhy = hasPadEvidence
                        ? "outside pad thresholds - committing"
                        : "no pad evidence - committing";
                    string commitReason = hasPadEvidence
                        ? "outside-pad-thresholds"
                        : "no-pad-evidence";
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split - {rec.RecordingId} ({dur:F1}s, {maxDist:F0}m) " +
                        $"{commitWhy} reason={commitReason}");
                }
            }

            string recId = rec.RecordingId;
            double sUT = rec.StartUT;
            double eUT = rec.EndUT;
            TreeCommitService.CommitRecordingDirect(rec);
            double ledgerStartUT = LedgerOrchestrator.ResolveStandaloneCommitWindowStartUt(rec, sUT);
            int pendingBefore = GameStateRecorder.PendingScienceSubjects.Count;
            IReadOnlyList<PendingScienceSubject> pendingForCommit =
                LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                    GameStateRecorder.PendingScienceSubjects,
                    recId,
                    ledgerStartUT,
                    eUT);
            bool commitSucceeded = false;
            bool scienceAddedToLedger = false;
            try
            {
                LedgerOrchestrator.OnRecordingCommitted(
                    recId,
                    ledgerStartUT,
                    eUT,
                    pendingForCommit,
                    ref scienceAddedToLedger);
                commitSucceeded = true;
            }
            finally
            {
                LedgerOrchestrator.FinalizeScopedPendingScienceCommit(
                    "Flight",
                    "FallbackCommitSplitRecorder",
                    pendingBefore,
                    pendingForCommit,
                    commitSucceeded,
                    scienceAddedToLedger);
            }
            // #390: prune consumed events after milestone creation + ledger conversion
            GameStateStore.PruneProcessedEvents();

            ParsekLog.Info("Flight", "FallbackCommitSplitRecorder: recording committed");
        }

        /// <summary>
        /// Resumes recording after a false-alarm split detection (debris undock, joint break
        /// that didn't produce a trackable vessel, deduplication skip). Re-enables the
        /// stopped recorder and restores it as the active recorder.
        /// </summary>
        internal void ResumeSplitRecorder(FlightRecorder splitRec, string reason, bool keepRecordedVessel = false)
        {
            RecorderStateLog.RecState("ResumeSplitRecorder:entry", host.CaptureRecorderState());
            if (splitRec == null)
            {
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: no recorder to resume ({reason})");
                return;
            }

            // Don't resume on a destroyed vessel — fall back to commit
            if (splitRec.VesselDestroyedDuringRecording)
            {
                ParsekLog.Info("Flight", $"ResumeSplitRecorder: vessel was destroyed ({reason}) — committing instead of resuming");
                FallbackCommitSplitRecorder(splitRec);
                return;
            }

            splitRec.ResumeAfterFalseAlarm(keepRecordedVessel);

            if (splitRec.IsRecording)
            {
                host.recorder = splitRec;
                // Restore tree mode if tree is active
                if (host.activeTree != null)
                    host.recorder.ActiveTree = host.activeTree;
                ParsekLog.Info("Flight", $"Recording resumed after false alarm: {reason}");
            }
            else
            {
                // ResumeAfterFalseAlarm failed (no active vessel?) — fall back to commit
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: resume failed ({reason}) — falling back to commit");
                FallbackCommitSplitRecorder(splitRec);
            }
        }

        /// <summary>
        /// Checks the deduplication guard. Returns true if this branch should proceed.
        /// </summary>
        internal bool CheckBranchDeduplication(double branchUT, uint newVesselPid)
        {
            if (Math.Abs(branchUT - host.lastBranchUT) > 0.1)
            {
                host.lastBranchVesselPids.Clear();
                host.lastBranchUT = branchUT;
            }
            if (host.lastBranchVesselPids.Contains(newVesselPid))
            {
                ParsekLog.Verbose("Flight", $"Skipping duplicate branch for pid={newVesselPid} at same UT");
                return false;
            }
            host.lastBranchVesselPids.Add(newVesselPid);
            return true;
        }

        /// <summary>
        /// Bug #263 safety net: for each new vessel created by a joint break, emits a fallback
        /// <c>Decoupled</c> PartEvent on the recorder for the vessel's root part (if not already
        /// decoupled). This catches decouple events that raced past the recorder's joint break
        /// handler when a symmetry group of decouplers fires.
        ///
        /// Resolves the new vessel from live <see cref="FlightGlobals.Vessels"/> first, falling
        /// back to <see cref="decoupleCreatedVessels"/> (captured synchronously during the split)
        /// for debris that KSP may have already cleaned up by the deferred-check frame.
        /// </summary>
        /// <param name="rec">The (resumed) recorder to append events to.</param>
        /// <param name="newVesselPids">Vessel PIDs that appeared as a result of the joint break.</param>
        /// <param name="branchUT">Universal time to stamp on the fallback events.</param>
        /// <returns>Number of new events added (0 if all pids were already in the recorder's decoupled set).</returns>
        internal int EmitFallbackDecoupleEventsForNewVessels(
            FlightRecorder rec, List<uint> newVesselPids, double branchUT)
        {
            if (rec == null || newVesselPids == null || newVesselPids.Count == 0)
                return 0;

            int added = 0;
            for (int i = 0; i < newVesselPids.Count; i++)
            {
                uint newPid = newVesselPids[i];

                // Resolve the new vessel. Prefer live FlightGlobals lookup; fall back to
                // decoupleCreatedVessels captured synchronously during the split event.
                Vessel newVessel = FlightRecorder.FindVesselByPid(newPid);
                if (newVessel == null && host.decoupleCreatedVessels != null)
                {
                    for (int j = 0; j < host.decoupleCreatedVessels.Count; j++)
                    {
                        Vessel dv = host.decoupleCreatedVessels[j];
                        if (dv != null && dv.persistentId == newPid)
                        {
                            newVessel = dv;
                            break;
                        }
                    }
                }

                if (newVessel == null)
                {
                    ParsekLog.Verbose("Flight",
                        $"EmitFallbackDecoupleEventsForNewVessels: vessel pid={newPid} not found, skipping");
                    continue;
                }

                Part rootPart = newVessel.rootPart;
                if (rootPart == null)
                {
                    ParsekLog.Verbose("Flight",
                        $"EmitFallbackDecoupleEventsForNewVessels: vessel pid={newPid} has no rootPart, skipping");
                    continue;
                }

                added += rec.RecordFallbackDecoupleEvent(
                    rootPart.persistentId,
                    rootPart.partInfo?.name,
                    branchUT);
            }

            return added;
        }

        /// <summary>
        /// Called each frame from Update() to check the crash coalescer for pending breakup events.
        /// When the coalescing window expires, emits a BREAKUP branch point on the active tree.
        /// </summary>
        internal void TickCrashCoalescer()
        {
            if (host.crashCoalescer == null || !host.crashCoalescer.HasPendingBreakup)
                return;

            double currentUT = Planetarium.GetUniversalTime();
            var breakupBp = host.crashCoalescer.Tick(currentUT);
            if (breakupBp == null)
                return;

            ParsekLog.Info("Coalescer",
                $"Coalescer emitted split branch point: ut={breakupBp.UT:F2}, " +
                $"type={breakupBp.Type}, cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, " +
                $"debris={breakupBp.DebrisCount}, duration={breakupBp.BreakupDuration:F3}s");

            ProcessBreakupEvent(breakupBp);
        }

        /// <summary>
        /// Emits a split the crash coalescer is still holding, before the active recording
        /// stops being active (its vessel goes to the background recorder on a focus
        /// change). <see cref="ProcessBreakupEvent"/> attaches the branch point to
        /// <c>activeTree.ActiveRecordingId</c>, which a focus change clears, so a split
        /// emitted after the hand-off would be dropped and the new vessel would get no
        /// child recording. Emitting it early only shortens the coalescing window.
        /// </summary>
        internal void EmitPendingSplitBeforeFocusLeaves(string reason)
        {
            if (host.crashCoalescer == null || !host.crashCoalescer.HasPendingBreakup)
                return;
            if (host.activeTree == null || string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
                return;

            double windowStartUT = host.crashCoalescer.WindowStartUT;
            var breakupBp = host.crashCoalescer.EmitNow();
            if (breakupBp == null)
                return;

            ParsekLog.Info("Coalescer",
                $"Coalescer emitted split branch point early (focus leaving the active recording, {reason}): " +
                $"ut={breakupBp.UT.ToString("F2", CultureInfo.InvariantCulture)}, type={breakupBp.Type}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"windowAgeSeconds={(Planetarium.GetUniversalTime() - windowStartUT).ToString("F2", CultureInfo.InvariantCulture)}");

            ProcessBreakupEvent(breakupBp);
        }

        internal static string DescribeBreakupChildSeedPose(
            TrajectoryPoint? seedPoint,
            Vessel liveVessel,
            double liveUT)
        {
            if (!seedPoint.HasValue)
                return "seedPoint=none";

            var ic = CultureInfo.InvariantCulture;
            TrajectoryPoint point = seedPoint.Value;
            string summary =
                $"seedPoint@{point.ut.ToString("F2", ic)} body={point.bodyName} " +
                $"flags={point.flags} " +
                $"lla=({point.latitude.ToString("F2", ic)}," +
                $"{point.longitude.ToString("F2", ic)}," +
                $"{point.altitude.ToString("F2", ic)}) " +
                $"vel={ParsekFlight.FormatVector3(point.velocity)}";

            Part rootPart = liveVessel?.rootPart;
            if (rootPart == null)
                return summary;

            CelestialBody body = liveVessel.mainBody;
            if ((body == null || !string.Equals(body.name, point.bodyName, StringComparison.Ordinal))
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, point.bodyName, StringComparison.Ordinal));
            }

            if (body == null || rootPart.transform == null)
                return summary;

            Vector3d seedWorld = body.GetWorldSurfacePosition(
                point.latitude,
                point.longitude,
                point.altitude);
            Vector3d rootWorld = rootPart.transform.position;
            Vector3d seedRootDelta = seedWorld - rootWorld;

            return summary + " " +
                $"liveRootUT={liveUT.ToString("F2", ic)} " +
                $"rootPart={rootPart.partInfo?.name ?? rootPart.name} pid={rootPart.persistentId} " +
                $"rootLla=({body.GetLatitude(rootWorld).ToString("F2", ic)}," +
                $"{body.GetLongitude(rootWorld).ToString("F2", ic)}," +
                $"{body.GetAltitude(rootWorld).ToString("F2", ic)}) " +
                $"seed-liveRoot={ParsekFlight.FormatVector3d(seedRootDelta)} " +
                $"seedLiveRootDist={seedRootDelta.magnitude.ToString("F2", ic)}m";
        }

        internal static TrajectoryPoint? ResolveControlledChildInitialTrajectoryPoint(
            uint pid,
            TrajectoryPoint? capturedPoint,
            Vessel liveVessel,
            double liveUT)
        {
            if (liveVessel == null)
            {
                if (capturedPoint.HasValue)
                {
                    ParsekLog.Verbose("Coalescer",
                        $"Controlled child initial seed: pid={pid} source=decouple-callback " +
                        $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                        "reason=live-vessel-missing");
                }
                return capturedPoint;
            }

            TrajectoryPoint livePoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                liveVessel,
                liveUT,
                preferRootPartSurfacePose: true);

            if (!capturedPoint.HasValue)
            {
                ParsekLog.Info("Coalescer",
                    $"Controlled child initial seed: pid={pid} source=live-current " +
                    $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    "reason=no-captured-seed");
                return livePoint;
            }

            double seedLiveRootDistance;
            double propagatedSeedResidual;
            bool hasDistance = TryComputeBreakupSeedLiveRootDistance(
                capturedPoint.Value,
                liveVessel,
                liveUT,
                out seedLiveRootDistance,
                out propagatedSeedResidual);
            bool preferLive = ParsekFlight.ShouldPreferLiveBreakupChildSeed(
                childHasController: true,
                liveVesselAvailable: true,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: hasDistance ? seedLiveRootDistance : double.NaN,
                propagatedSeedLiveRootResidualMeters: hasDistance ? propagatedSeedResidual : double.NaN);

            if (!preferLive)
            {
                ParsekLog.Verbose("Coalescer",
                    $"Controlled child initial seed: pid={pid} source=decouple-callback " +
                    $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"seedLiveRootDist={(hasDistance ? seedLiveRootDistance.ToString("F2", CultureInfo.InvariantCulture) : "n/a")}m " +
                    $"propagatedResidual={(hasDistance ? propagatedSeedResidual.ToString("F2", CultureInfo.InvariantCulture) : "n/a")}m " +
                    $"threshold={ParsekFlight.ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m");
                return capturedPoint;
            }

            bool directSeedMissesLiveRoot =
                ParsekFlight.IsFinite(seedLiveRootDistance)
                && seedLiveRootDistance > ParsekFlight.ControlledChildLiveSeedResidualToleranceMeters;
            string replacementReason = directSeedMissesLiveRoot
                ? "seed-misses-live-root"
                : "propagated-seed-misses-live-root";
            ParsekLog.Info("Coalescer",
                $"Controlled child initial seed replaced with live sample: pid={pid} " +
                $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"seedLiveRootDist={seedLiveRootDistance.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"propagatedResidual={propagatedSeedResidual.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"threshold={ParsekFlight.ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"source=live-root-part reason={replacementReason}");
            return livePoint;
        }

        internal static bool TryComputeBreakupSeedLiveRootDistance(
            TrajectoryPoint seedPoint,
            Vessel liveVessel,
            double liveUT,
            out double distanceMeters,
            out double propagatedResidualMeters)
        {
            distanceMeters = double.NaN;
            propagatedResidualMeters = double.NaN;

            Part rootPart = liveVessel?.rootPart;
            CelestialBody body = liveVessel?.mainBody;
            if (rootPart == null || rootPart.transform == null || body == null)
                return false;

            if (!string.Equals(body.name, seedPoint.bodyName, StringComparison.Ordinal)
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, seedPoint.bodyName, StringComparison.Ordinal));
            }

            if (body == null)
                return false;

            Vector3d seedWorld = body.GetWorldSurfacePosition(
                seedPoint.latitude,
                seedPoint.longitude,
                seedPoint.altitude);
            Vector3d rootWorld = rootPart.transform.position;
            distanceMeters = (seedWorld - rootWorld).magnitude;
            propagatedResidualMeters = ParsekFlight.ComputeBreakupSeedPropagatedResidualMeters(
                seedWorld,
                seedPoint.velocity,
                seedPoint.ut,
                rootWorld,
                liveUT);
            return ParsekFlight.IsFinite(distanceMeters) && ParsekFlight.IsFinite(propagatedResidualMeters);
        }

        /// <summary>
        /// Processes a BREAKUP branch point emitted by the crash coalescer.
        /// Adds the BREAKUP event as a branch point on the active tree recording and
        /// creates child recordings for debris and controlled vessels.
        /// If no active tree exists, the breakup is not recorded.
        /// </summary>
        internal void ProcessBreakupEvent(BranchPoint breakupBp)
        {
            if (host.activeTree == null)
            {
                ParsekLog.Info("Coalescer",
                    "ProcessBreakupEvent: no active tree — breakup not recorded. " +
                    $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}");
                return;
            }

            string activeRecId = host.activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(activeRecId))
            {
                ParsekLog.Warn("Coalescer",
                    "ProcessBreakupEvent: active tree has no active recording — cannot attach breakup event");
                return;
            }

            Recording activeRec;
            if (!host.activeTree.Recordings.TryGetValue(activeRecId, out activeRec))
            {
                ParsekLog.Warn("Coalescer",
                    $"ProcessBreakupEvent: active recording id={activeRecId} not found in tree");
                return;
            }

            ParsekFlight.WireBreakupIntoTree(host.activeTree, activeRecId, activeRec, breakupBp);

            // Refresh the vessel snapshot to reflect post-breakup state.
            // The recording continues past breakup (breakup-continuous design), so the
            // spawn snapshot must show the surviving vessel, not the pre-breakup config.
            // Without this, spawning materializes the full pre-breakup rocket. (#224)
            //
            // Bug #271: in always-tree mode, the tree recording's trajectory points are
            // still in the recorder buffer (not flushed to the Recording object), so
            // SnapshotVessel's Points.Count==0 guard would skip the snapshot. Use
            // TryBackupSnapshot directly — the distance calculation is not needed for
            // a mid-flight snapshot refresh.
            //
            // The surviving parent is the vessel the active recorder records. It is not
            // always the active vessel: a split can move focus to the vessel that came off
            // before the hand-off to the background recorder (EmitPendingSplitBeforeFocusLeaves).
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint parentVesselPid = ParsekFlight.ResolveBreakupParentVesselPid(
                host.recorder != null ? host.recorder.RecordingVesselId : 0u,
                activeVessel != null ? activeVessel.persistentId : 0u);
            Vessel parentVessel = activeVessel != null && activeVessel.persistentId == parentVesselPid
                ? activeVessel
                : FlightRecorder.FindVesselByPid(parentVesselPid);
            if (parentVessel != null && parentVessel.persistentId == activeRec.VesselPersistentId)
            {
                var snap = FlightRecorder.TryBackupSnapshot(parentVessel);
                if (snap != null)
                    activeRec.VesselSnapshot = snap;
                ParsekLog.Info("Coalescer",
                    $"ProcessBreakupEvent: refreshed VesselSnapshot post-breakup " +
                    $"(parts={parentVessel.parts?.Count ?? 0}, snapshot={snap != null})");
            }

            // Bug #298: snapshot active recorder's engine/RCS state for child recordings.
            // The recorder is still running (breakup-continuous design), so its state is live.
            InheritedEngineState? breakupEngineState = host.recorder != null
                ? InheritedEngineState.FromRecorder(host.recorder) : null;

            TrajectoryPoint? focusedParentSeedAnchorPoint = null;
            if (host.recorder != null
                && !host.recorder.CurrentTrackSectionUsesRelativeFrame
                && FlightRecorder.TryFindStructuralEventSnapshotPointForUT(
                    host.recorder?.Recording,
                    breakupBp.UT,
                    out TrajectoryPoint parentSeedAnchorPoint))
            {
                focusedParentSeedAnchorPoint = parentSeedAnchorPoint;
                ParsekLog.Verbose("Coalescer",
                    $"ProcessBreakupEvent: focused parent seed anchor point available: " +
                    $"parentRec={activeRecId} ut={parentSeedAnchorPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"flags={parentSeedAnchorPoint.flags}");
            }
            else
            {
                ParsekLog.Verbose("Coalescer",
                    $"ProcessBreakupEvent: focused parent seed anchor point unavailable: " +
                    $"parentRec={activeRecId} breakupUT={breakupBp.UT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"recorderPoints={host.recorder?.Recording?.Count ?? 0} " +
                    $"currentSectionRelative={host.recorder?.CurrentTrackSectionUsesRelativeFrame ?? false}");
            }

            // Create child recording segments for controlled children (vessels with probe
            // cores that survive the breakup).
            // These are NOT debris — they record indefinitely (no TTL) and can serve as
            // RELATIVE anchors during playback.
            var controlledChildren = host.crashCoalescer.LastEmittedControlledChildPids;

            // Phase 4 (Rewind-to-Staging): collect (vessel, child recording) pairs
            // for the controllable outputs of the breakup. The parent recording
            // carrying the active vessel is also a controllable output (it survives
            // the breakup in breakup-continuous design), so it participates in the
            // multi-controllable classifier alongside the children.
            var breakupControllablePairs = new List<(Vessel vessel, Recording rec)>();
            if (parentVessel != null)
                breakupControllablePairs.Add((parentVessel, activeRec));

            if (controlledChildren != null && controlledChildren.Count > 0)
            {
                for (int i = 0; i < controlledChildren.Count; i++)
                {
                    uint pid = controlledChildren[i];
                    Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                    // Keep the split-time ghost snapshot even when the child vessel is
                    // still alive 0.5s later; otherwise the ghost builds from a later
                    // post-split snapshot and can appear spatially ahead on frame 1.
                    ConfigNode ctrlSnap = host.crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ParsekFlight.ApplyStructuralEventFlagToChildSeed(
                        host.crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);

                    // Skip dead-on-arrival controlled children: once the live
                    // Vessel is gone before the coalescer window expires there
                    // is no controlled continuation to sample. A pre-captured
                    // snapshot only turns that into a destroyed one-point
                    // "Unknown" row, so leave the split represented by the
                    // parent BREAKUP branch point.
                    if (ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                            childVesselIsAlive: childVessel != null,
                            hasPreCapturedSnapshot: ctrlSnap != null))
                    {
                        ParsekLog.Info("Coalescer",
                            ParsekFlight.BuildDeadOnArrivalControlledChildSkipLog(
                                pid,
                                hasPreCapturedSnapshot: ctrlSnap != null));
                        continue;
                    }

                    TrajectoryPoint? initialPoint = ResolveControlledChildInitialTrajectoryPoint(
                        pid,
                        breakupChildPoint,
                        childVessel,
                        Planetarium.GetUniversalTime());
                    initialPoint = ParsekFlight.ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);

                    var childRec = ParsekFlight.CreateBreakupChildRecording(host.activeTree, breakupBp, pid, childVessel, false, "Unknown",
                        ctrlSnap, initialPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL - records indefinitely)
                    if (childVessel != null && host.backgroundRecorder != null)
                    {
                        host.activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        // Parent-anchored controlled-decoupled children carry the same
                        // structural-event parent-anchor seed as debris siblings so the
                        // first BG sample after split lands on the deterministic split-
                        // moment parent pose rather than relying on a post-split live
                        // resolve. The debris loop below queues the same seed at line 6470.
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            host.backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        host.backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                    }

                    if (childVessel != null && childRec != null)
                        breakupControllablePairs.Add((childVessel, childRec));

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: controlled child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={childVessel != null}, bgRecorder={host.backgroundRecorder != null} " +
                        $"{DescribeBreakupChildSeedPose(initialPoint, childVessel, Planetarium.GetUniversalTime())}");
                }
            }

            // Phase 4 (design §5.1 + §7.1 + §7.2 + §7.19): if this breakup produced
            // >=2 controllable outputs (counting the surviving parent), author a
            // RewindPoint. The author computes PidSlotMap / RootPartPidMap on the
            // deferred frame and writes the quicksave atomically. §7.1 prunes the
            // single-controllable case (no RP for debris-only breakups).
            TryAuthorRewindPointForBreakup(breakupBp, breakupControllablePairs);

            // Also create debris child recordings for new debris from this breakup
            int skippedDebris = 0;
            var debrisPids = host.crashCoalescer.LastEmittedDebrisPids;
            if (debrisPids != null && debrisPids.Count > 0)
            {
                double debrisExpiryUT = breakupBp.UT + BackgroundRecorder.DebrisTTLSeconds;
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    uint pid = debrisPids[i];
                    Vessel debrisVessel = FlightRecorder.FindVesselByPid(pid);

                    if (debrisVessel != null && !ParsekFlight.ShouldRecordDebris(debrisVessel))
                    {
                        skippedDebris++;
                        continue;
                    }

                    // Use the coalescer's split-time snapshot for ghost visuals even when
                    // the debris vessel survives the coalescing window; VesselSnapshot still
                    // comes from the later live state for spawning.
                    ConfigNode preSnap = host.crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ParsekFlight.ApplyStructuralEventFlagToChildSeed(
                        host.crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);
                    var childRec = ParsekFlight.CreateBreakupChildRecording(host.activeTree, breakupBp, pid, debrisVessel, true, "Debris",
                        preSnap, breakupChildPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    if (debrisVessel != null && host.backgroundRecorder != null)
                    {
                        TrajectoryPoint? initialPoint = breakupChildPoint;
                        if (!initialPoint.HasValue)
                        {
                            double sampleUT = Planetarium.GetUniversalTime();
                            initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                debrisVessel, sampleUT, preferRootPartSurfacePose: true);
                        }
                        initialPoint = ParsekFlight.ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);
                        host.activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            host.backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        host.backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                        host.backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: debris child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={debrisVessel != null} " +
                        $"{DescribeBreakupChildSeedPose(breakupChildPoint, debrisVessel, Planetarium.GetUniversalTime())}");
                }
            }

            ParsekLog.Info("Coalescer",
                $"ProcessBreakupEvent: {breakupBp.Type} attached to tree={host.activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"skippedTrivial={skippedDebris}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");
        }

        internal void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewBoardVessel:entry", host.CaptureRecorderState());
            if (host.activeTree == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active tree — ignoring");
                return;
            }
            if (host.pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "pendingSplitInProgress",
                    data.from?.vessel?.persistentId ?? 0u,
                    data.to?.vessel?.persistentId ?? 0u);
                return; // split must complete first
            }
            if (data.to?.vessel == null)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "target-vessel-null",
                    data.from?.vessel?.persistentId ?? 0u,
                    0u);
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: target vessel is null — ignoring");
                return;
            }

            host.pendingBoardingTargetPid = data.to.vessel.persistentId;
            host.boardingConfirmFrames = 0;

            // Store whether the boarding target is in the tree
            host.pendingBoardingTargetInTree = (host.activeTree != null &&
                host.activeTree.BackgroundMap.ContainsKey(data.to.vessel.persistentId));

            host.Log($"onCrewBoardVessel: target vessel pid={host.pendingBoardingTargetPid}" +
                (host.activeTree != null ? $", inTree={host.pendingBoardingTargetInTree}" : ""));

            TryPromoteEvaRecordingForBoardMerge(data.from?.vessel);
        }

        /// <summary>
        /// Rebinds the live recorder to a boarding kerbal's EVA branch recording when that
        /// recording is still only background-tracked, so the board merge can run.
        /// </summary>
        /// <remarks>
        /// An EVA branch parks the kerbal's recording in <c>BackgroundMap</c> and leaves the
        /// promotion to the post-switch first-modification watcher. A kerbal that exits and
        /// re-boards without moving (hatch/ladder hop, no release) never trips a trigger, so
        /// the watcher is still armed when the board arrives: the boarding detection in
        /// <see cref="OnVesselSwitchComplete"/> requires <c>recorder.IsRecording</c> and
        /// <see cref="HandleTreeBoardMerge"/> requires the EVA recording to be
        /// <c>activeTree.ActiveRecordingId</c>, so without this rebind the Board branch
        /// point is silently dropped and the EVA recording terminates as Destroyed instead
        /// of Boarded. onCrewBoardVessel fires before KSP's focus switch, so the EVA kerbal
        /// is still the active vessel here and the normal promotion path applies.
        /// </remarks>
        private void TryPromoteEvaRecordingForBoardMerge(Vessel evaVessel)
        {
            uint evaPid = evaVessel != null ? evaVessel.persistentId : 0u;
            uint activePid = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.persistentId
                : 0u;
            string trackedRecordingId = null;
            bool trackedInBackground = host.activeTree != null && evaPid != 0
                && host.activeTree.BackgroundMap.TryGetValue(evaPid, out trackedRecordingId);

            var decision = ParsekFlight.DecideEvaBoardPromotion(
                hasActiveTree: host.activeTree != null,
                restoringActiveTree: ParsekFlight.restoringActiveTree,
                sourceIsEva: evaVessel != null && evaVessel.isEVA,
                sourceIsGhostVessel: evaPid != 0 && GhostMapPresence.IsGhostMapVessel(evaPid),
                sourceVesselPid: evaPid,
                hasLiveRecorder: host.recorder != null,
                liveRecorderVesselPid: host.recorder != null ? host.recorder.RecordingVesselId : 0u,
                activeVesselPid: activePid,
                sourceTrackedInBackground: trackedInBackground,
                trackedRecordingId: trackedRecordingId,
                treeActiveRecordingId: host.activeTree != null ? host.activeTree.ActiveRecordingId : null);

            if (decision != ParsekFlight.EvaBoardPromotionDecision.Promote)
            {
                ParsekLog.Verbose("Flight",
                    $"EVA board promotion skipped: decision={decision} evaPid={evaPid} " +
                    $"activePid={activePid} targetPid={host.pendingBoardingTargetPid} " +
                    $"tracked={trackedInBackground} " +
                    $"treeActiveRec={host.activeTree?.ActiveRecordingId ?? "-"} " +
                    $"trackedRec={trackedRecordingId ?? "-"}");
                return;
            }

            ParsekLog.Info("Flight",
                $"EVA board promotion: rebinding background EVA recording " +
                $"'{trackedRecordingId}' to the live recorder before the board merge " +
                $"(evaPid={evaPid}, targetPid={host.pendingBoardingTargetPid})");

            host.backgroundRecorder?.OnVesselRemovedFromBackground(evaPid);
            host.PromoteRecordingFromBackground(trackedRecordingId, evaVessel);

            if (host.recorder != null && host.recorder.IsRecording)
                host.PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("eva board promotion");
            else
            {
                ParsekLog.Warn("Flight",
                    $"EVA board promotion failed to start a recorder for evaPid={evaPid} - " +
                    $"board merge will be skipped");
            }
        }

        private bool TryStartEvaBranchFromBackgroundParent(GameEvents.FromToAction<Part, Part> data)
        {
            uint sourcePid = data.from?.vessel?.persistentId ?? 0u;
            uint targetPid = data.to?.vessel?.persistentId ?? 0u;

            // Only the structural preconditions matter before we can safely dereference activeTree/sourceVessel.
            var initialDecision = ParsekFlight.DecideEvaBackgroundParentRoute(
                isRecording: host.IsRecording,
                pendingSplitInProgress: host.pendingSplitInProgress,
                hasActiveTree: host.activeTree != null,
                hasSourceVessel: data.from?.vessel != null,
                trackedParentResolved: false,
                mappedRecordingExists: false,
                hasCrewName: false);

            if (initialDecision == ParsekFlight.EvaBackgroundParentRouteDecision.NotHandled)
                return false;

            if (initialDecision == ParsekFlight.EvaBackgroundParentRouteDecision.HandledSkipPendingSplit)
            {
                LogSplitSkip(
                    "OnCrewOnEva",
                    "pendingSplitInProgress",
                    sourcePid,
                    targetPid);
                host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            Vessel sourceVessel = data.from?.vessel;
            string parentRecordingId;
            string diagnostic;
            bool trackedParentResolved = ParsekFlight.TryResolveTrackedBackgroundParentRecording(
                    host.activeTree,
                    sourceVessel.persistentId,
                    out parentRecordingId,
                    out diagnostic);
            bool mappedRecordingExists = !string.IsNullOrEmpty(parentRecordingId)
                && host.activeTree.Recordings != null
                && host.activeTree.Recordings.ContainsKey(parentRecordingId);
            string kerbalName = ExtractEvaKerbalName(data);

            var decision = ParsekFlight.DecideEvaBackgroundParentRoute(
                isRecording: false,
                pendingSplitInProgress: false,
                hasActiveTree: true,
                hasSourceVessel: true,
                trackedParentResolved: trackedParentResolved,
                mappedRecordingExists: mappedRecordingExists,
                hasCrewName: !string.IsNullOrEmpty(kerbalName));

            if (decision == ParsekFlight.EvaBackgroundParentRouteDecision.HandledUntrackedSource)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is not the live recorder " +
                    $"and is not a tracked background parent ({diagnostic}); suppressing deferred auto-record");
                host.ScreenMessage("Recording blocked (EVA source not tracked)", 2f);
                host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == ParsekFlight.EvaBackgroundParentRouteDecision.HandledInvalidMapEntry)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: background parent map entry is invalid " +
                    $"(sourcePid={sourceVessel.persistentId}, parentRec={parentRecordingId ?? "null"}, " +
                    $"diagnostic={diagnostic}); suppressing deferred auto-record");
                host.ScreenMessage("Recording blocked (invalid EVA tree state)", 2f);
                host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == ParsekFlight.EvaBackgroundParentRouteDecision.HandledMissingCrewName)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is tracked in background " +
                    "but EVA kerbal name could not be extracted; suppressing deferred auto-record");
                host.ScreenMessage("Recording blocked (EVA crew unknown)", 2f);
                host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision != ParsekFlight.EvaBackgroundParentRouteDecision.StartDeferredBackgroundParentBranch)
                return false;

            host.PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
            host.pendingSplitInProgress = true;
            host.Log($"Background-parent EVA detected: '{kerbalName}' from pid={sourceVessel.persistentId} " +
                $"parentRec={parentRecordingId} - starting deferred tree branch");
            host.StartCoroutine(host.DeferredEvaBranchFromBackgroundParent(
                parentRecordingId,
                sourceVessel.persistentId,
                kerbalName,
                data));
            return true;
        }

        internal void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewOnEva:entry", host.CaptureRecorderState());
            // Mid-recording EVA: tree branch (replaces legacy chain continuation)
            if (host.IsRecording)
            {
                if (host.pendingSplitInProgress)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "pendingSplitInProgress",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return; // another split is being processed
                }

                double evaEventUT = Planetarium.GetUniversalTime();
                var evaInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) evaInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    evaInvolved.Add(data.to.vessel);
                host.backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Only trigger if EVA is from the vessel we're recording
                if (data.from?.vessel == null ||
                    data.from.vessel.persistentId != host.recorder.RecordingVesselId)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        data.from?.vessel == null ? "source-vessel-null" : "source-vessel-mismatch",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return;
                }

                string kerbalName = ExtractEvaKerbalName(data);
                if (string.IsNullOrEmpty(kerbalName))
                {
                    host.Log("EVA during recording but could not extract kerbal name — ignoring");
                    return;
                }

                // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at
                // the exact EVA UT for the recorded vessel (the parent capsule). The
                // EVA kerbal-as-vessel is also "involved" per §12; AppendStructuralEventSnapshot
                // dedups by RecordingVesselId so only the parent capsule's snapshot is
                // committed in the typical case where the parent is the recorded vessel.
                host.recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Tree mode: stop recorder synchronously, defer tree branch creation
                host.recorder.StopRecordingForChainBoundary();
                host.pendingSplitRecorder = host.recorder;
                host.recorder = null;
                host.pendingSplitInProgress = true;
                host.Log($"Mid-recording EVA detected: '{kerbalName}' — starting deferred tree branch");
                host.StartCoroutine(host.DeferredEvaBranch(kerbalName, data));
                return;
            }

            if (!host.IsRecording && TryStartEvaBranchFromBackgroundParent(data))
                return;

            bool hasSourceVessel = data.from?.vessel != null;
            if (!ParsekFlightPostSwitchAutoRecord.ShouldQueueAutoRecordOnEva(
                hasSourceVessel,
                autoRecordOnEvaEnabled: ParsekSettings.Current?.autoRecordOnEva != false))
            {
                if (!hasSourceVessel)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "source-vessel-null",
                        0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: source vessel is null — ignoring");
                }
                else
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            host.pendingAutoRecord = true;
            host.Log($"EVA detected (sit={data.from.vessel.situation}) — pending auto-record");
        }

        private void LogSplitSkip(string source, string reason, uint sourcePid, uint targetPid)
        {
            string activeRecordingId = host.activeTree?.ActiveRecordingId;
            string identity = string.Format(CultureInfo.InvariantCulture,
                "split-skip-{0}-{1}-{2}-{3}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(activeRecordingId) ? "none" : activeRecordingId,
                sourcePid,
                targetPid);
            string stateKey = string.Format(CultureInfo.InvariantCulture,
                "{0}|pending={1}",
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                host.pendingSplitInProgress ? 1 : 0);
            ParsekLog.VerboseOnChange("Flight",
                identity,
                stateKey,
                ParsekFlightPostSwitchAutoRecord.FormatSplitSkipSummary(
                    source,
                    reason,
                    activeRecordingId,
                    sourcePid,
                    targetPid,
                    host.pendingSplitInProgress));
        }

        internal static string ExtractEvaKerbalName(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to == null) return null;
            var crew = data.to.protoModuleCrew;
            if (crew != null && crew.Count > 0) return crew[0].name;
            if (data.to.vessel != null) return data.to.vessel.vesselName;
            return null;
        }

        internal void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            // Vessel names (design 7.7): stock fires onPartCouple from Part.Couple before the
            // merge, with data.from on the vessel being absorbed (ModuleDockingNode.DockToVessel
            // couples its own part to the dominant's). Recorded whatever the recorder state.
            VesselNameLiveSync.OnDockCouple(data.from?.vessel, data.to?.vessel);

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder.
            // OnPartCouple's own null checks (L3784, L3845) already prevent most interference,
            // but the narrow window after recorder.StartRecording in the coroutine could allow
            // a dock event on the just-restored recorder before the coroutine completes.
            if (ParsekFlight.restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnPartCouple: skipped — restore coroutine in progress");
                return;
            }

            RecorderStateLog.RecState("OnPartCouple:entry", host.CaptureRecorderState());
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

            // Classify the connection producer NOW, while both event parts still
            // resolve (design-logistics-claw-producer.md 2.1). A claw couple fires
            // the same onPartCouple as a dock (findings 1.2) but must be stamped
            // Grapple, and an unrecognized (modded) coupling producer is stamped
            // Unknown so route admission fails closed instead of treating it as a
            // dock. An EVA kerbal grab is a real couple but never a logistics
            // transfer: suppress its route window by dropping the route target.
            RouteConnectionKind coupleTransferKind =
                ConnectionProducerClassifier.Classify(data.from, data.to);
            bool coupleInvolvesEva =
                ConnectionProducerClassifier.InvolvesEvaVessel(data.from, data.to);
            ParsekLog.Info("Flight",
                $"OnPartCouple producer classified: kind={coupleTransferKind} " +
                $"fromPart={data.from?.partInfo?.name ?? "<null>"} " +
                $"toPart={data.to?.partInfo?.name ?? "<null>"} involvesEva={coupleInvolvesEva}");

            // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at the
            // exact dock UT for the recorded vessel. Done BEFORE
            // StopRecordingForChainBoundary so the snapshot lands in the active section
            // (the helper no-ops once IsRecording flips false). Both vessels in the
            // dock pair are passed; the focused recorder filters by RecordingVesselId
            // and the background recorder filters by BackgroundMap membership.
            //
            // Skip the snapshot when this couple is the abort half of a split-in-
            // progress: the tree-mode path below early-returns at the
            // `pendingSplitInProgress` guard without committing the merge, and a
            // synthetic flagged point would otherwise land in a recording whose
            // section is about to be torn down by the in-flight split. Mirror the
            // exact same guard so the snapshot only fires on couples that the
            // downstream merge path will actually act on.
            if (host.recorder != null && host.recorder.IsRecording && !host.pendingSplitInProgress)
            {
                double dockEventUT = Planetarium.GetUniversalTime();
                var dockInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) dockInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    dockInvolved.Add(data.to.vessel);
                host.recorder.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");
                host.backgroundRecorder?.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");

                // Capture partner pre-couple snapshot for route window endpoint baseline.
                // When data.from / data.to still reference distinct vessels (KSP has not
                // reparented yet), the partner is the side that isn't the recorder. The
                // route window's DOCK_ENDPOINT_RESOURCES must capture the partner's
                // pre-dock state; falling back to the post-couple merged-vessel snapshot
                // inflates the baseline with the transport's tank.
                host.pendingDockPartnerSnapshot = null;
                host.pendingDockPartnerSnapshotPid = 0u;
                host.pendingDockSelfSnapshot = null;
                host.pendingDockSelfSnapshotPid = 0u;
                if (data.from?.vessel != null && data.to?.vessel != null &&
                    data.from.vessel != data.to.vessel)
                {
                    Vessel partner = null;
                    if (data.from.vessel.persistentId == host.recorder.RecordingVesselId
                        && data.to.vessel.persistentId != host.recorder.RecordingVesselId)
                    {
                        partner = data.to.vessel;
                    }
                    else if (data.to.vessel.persistentId == host.recorder.RecordingVesselId
                        && data.from.vessel.persistentId != host.recorder.RecordingVesselId)
                    {
                        partner = data.from.vessel;
                    }
                    else
                    {
                        // Neither side matches the recorder's vessel (rare; e.g. the
                        // recorder was on a third vessel observing). Prefer the side
                        // whose pid will not be the surviving merged pid so the snapshot
                        // captures pre-couple state. data.to is the survivor in KSP.
                        partner = data.from.vessel.persistentId != mergedPid
                            ? data.from.vessel
                            : data.to.vessel;
                    }

                    if (partner != null && partner.parts != null && partner.parts.Count > 0)
                    {
                        host.pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partner);
                        host.pendingDockPartnerSnapshotPid = partner.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple partner snapshot " +
                            $"partnerPid={host.pendingDockPartnerSnapshotPid} parts={partner.parts.Count}");
                    }

                    // Capture the recorder's own (transport) pre-couple snapshot for the
                    // route window transport baseline (mirror of the partner snapshot above).
                    Vessel self = ResolveSelfVessel(data, host.recorder.RecordingVesselId);
                    if (self != null && self.parts != null && self.parts.Count > 0)
                    {
                        host.pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(self);
                        host.pendingDockSelfSnapshotPid = self.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple self snapshot " +
                            $"selfPid={host.pendingDockSelfSnapshotPid} parts={self.parts.Count}");
                    }
                }
            }

            // --- TREE MODE: create merge branch ---
            if (host.activeTree != null && host.recorder != null)
            {
                if (host.pendingSplitInProgress) return; // split must complete first

                if (host.recorder.IsRecording)
                {
                    bool isTarget = (host.recorder.RecordingVesselId == mergedPid);
                    uint absorbedPid;
                    uint fromPid = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPid = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEvent = ParsekFlight.ResolveDockPartnerPidFromEvent(
                        fromPid, toPid, host.recorder.RecordingVesselId);

                    if (isTarget)
                    {
                        // We are the TARGET -- the absorbed partner is the couple event's
                        // partner when it is a BackgroundMap member of this tree, else 0.
                        absorbedPid = ParsekFlight.ResolveTargetSideAbsorbedPid(
                            partnerPidFromEvent, mergedPid, host.activeTree.BackgroundMap.Keys);
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple target-side absorbed partner: absorbedPid={absorbedPid} " +
                            $"partnerPidFromEvent={partnerPidFromEvent} " +
                            $"partnerInBackgroundMap={host.activeTree.BackgroundMap.ContainsKey(partnerPidFromEvent)}");
                    }
                    else
                    {
                        // We are the INITIATOR -- we are being absorbed
                        absorbedPid = host.recorder.RecordingVesselId;
                    }

                    // Resolve the route-partner PID from the couple event directly.
                    // BackgroundMap is intra-tree only, so a partner with a committed
                    // recording from a prior tree is not a BackgroundMap member.
                    //
                    // Accept the partner as route-eligible when EITHER:
                    //  (a) the pre-couple partner snapshot was captured successfully
                    //      (the partner is a real KSP vessel with parts that was
                    //      distinct from the recorder at couple time — third-playtest
                    //      scenario where the destination is loaded from save state
                    //      but has no prior Parsek recording), OR
                    //  (b) the partner has a known Parsek recording in the active tree
                    //      or in committed recordings (yesterday's cross-tree case).
                    // Both paths trust real evidence; the stricter "must have prior
                    // recording" gate from the first iteration was over-conservative
                    // for the destination-loaded-from-save workflow.
                    bool partnerSnapshotCaptured = host.pendingDockPartnerSnapshot != null
                        && host.pendingDockPartnerSnapshotPid == partnerPidFromEvent
                        && partnerPidFromEvent != 0u;
                    bool partnerKnown = ParsekFlight.IsKnownDockPartnerForRoute(
                        partnerPidFromEvent,
                        host.recorder.RecordingVesselId,
                        host.activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligible = partnerPidFromEvent != 0u
                        && partnerPidFromEvent != host.recorder.RecordingVesselId
                        && (partnerSnapshotCaptured || partnerKnown);
                    uint routeTargetPid = ParsekFlight.SuppressRouteWindowForEvaGrab(
                        partnerEligible ? partnerPidFromEvent : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: "");
                    bool partnerIsEva = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEvent);
                    uint partnerStampPid = ParsekFlight.ResolveBranchPartnerStampPid(
                        partnerPidFromEvent, host.recorder.RecordingVesselId, partnerIsEva);
                    string partnerStampGuid = ParsekFlight.ResolveDockPartnerGuidFromEvent(
                        partnerStampPid,
                        fromPid, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPid, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve: selfPid={host.recorder.RecordingVesselId} " +
                        $"mergedPid={mergedPid} isTarget={isTarget} absorbedPid={absorbedPid} " +
                        $"partnerPidFromEvent={partnerPidFromEvent} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCaptured} partnerKnown={partnerKnown} " +
                        $"partnerEligible={partnerEligible} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEva} partnerPidStamped={partnerStampPid} " +
                        $"partnerGuidStamped={partnerStampGuid ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    // Stop recorder synchronously
                    host.recorder.StopRecordingForChainBoundary();

                    // Set pending state for tree merge
                    host.pendingTreeDockMerge = true;
                    host.pendingDockMergedPid = mergedPid;
                    host.pendingDockAbsorbedPid = absorbedPid;
                    host.pendingDockRouteTargetPid = routeTargetPid;
                    host.pendingDockPartnerPid = partnerStampPid;
                    host.pendingDockPartnerGuid = partnerStampGuid;
                    host.pendingDockTransferKind = coupleTransferKind;
                    host.dockConfirmFrames = 0;

                    // Race condition guard: prevent Task 6 from misclassifying absorption as destruction
                    if (absorbedPid != 0)
                        host.dockingInProgress.Add(absorbedPid);

                    host.Log($"onPartCouple (tree): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid}, isTarget={isTarget})");
                    return;
                }
                else if (!host.recorder.IsRecording && host.recorder.CaptureAtStop != null)
                {
                    // OnPhysicsFrame already stopped us (initiator, late event).
                    // Target-side recorders are stopped synchronously by us at the top of
                    // this handler, never by OnPhysicsFrame, so reaching here with a
                    // target-side recorder (recorder PID still equal to mergedPid) means
                    // some other code path got there first -- bail out of route capture
                    // rather than misroute the endpoint.
                    uint absorbedPid = host.recorder.RecordingVesselId;
                    if (absorbedPid == mergedPid)
                    {
                        ParsekLog.Warn("Flight",
                            $"onPartCouple (tree, retroactive): unexpected target-side pre-stop " +
                            $"mergedPid={mergedPid}; skipping route endpoint capture");
                        return;
                    }
                    // Mirror the recording-path resolver: derive partner from event,
                    // validate via known-recordings registry, and emit the same diagnostic.
                    uint fromPidR = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPidR = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEventR = ParsekFlight.ResolveDockPartnerPidFromEvent(
                        fromPidR, toPidR, host.recorder.RecordingVesselId);

                    // Gap §5.1: also try to capture a pre-couple partner snapshot in
                    // the retroactive path. By the time we reach this branch
                    // OnPhysicsFrame has already stopped the recorder, so KSP may
                    // have ALREADY reparented data.from.vessel onto data.to.vessel
                    // and the snapshot would be the merged vessel (over-counted). We
                    // only capture when data.from.vessel and data.to.vessel are
                    // still distinct; if they aren't, fallbacks downstream
                    // (TryDeriveEndpointPartPidsFromPartner) carry the accepted
                    // baseline-inflation risk noted in §5.1. Captured BEFORE the
                    // eligibility check so partnerSnapshotCaptured can vote yes.
                    host.pendingDockPartnerSnapshot = null;
                    host.pendingDockPartnerSnapshotPid = 0u;
                    host.pendingDockSelfSnapshot = null;
                    host.pendingDockSelfSnapshotPid = 0u;
                    if (data.from?.vessel != null && data.to?.vessel != null &&
                        data.from.vessel != data.to.vessel)
                    {
                        Vessel partnerR = data.from.vessel.persistentId != absorbedPid
                            ? data.from.vessel
                            : (data.to.vessel.persistentId != absorbedPid ? data.to.vessel : null);
                        if (partnerR != null && partnerR.parts != null && partnerR.parts.Count > 0)
                        {
                            host.pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partnerR);
                            host.pendingDockPartnerSnapshotPid = partnerR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple partner snapshot " +
                                $"partnerPid={host.pendingDockPartnerSnapshotPid} parts={partnerR.parts.Count}");
                        }

                        // Mirror: the recorder's own (transport) pre-couple snapshot. In the
                        // retroactive path the recorder is already stopped and KSP may have
                        // reparented; this only fires while from/to are still distinct, else
                        // it stays null and the merged snapshot is used (accepted §5.1 risk).
                        Vessel selfR = ResolveSelfVessel(data, host.recorder.RecordingVesselId);
                        if (selfR != null && selfR.parts != null && selfR.parts.Count > 0)
                        {
                            host.pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(selfR);
                            host.pendingDockSelfSnapshotPid = selfR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple self snapshot " +
                                $"selfPid={host.pendingDockSelfSnapshotPid} parts={selfR.parts.Count}");
                        }
                    }

                    // Eligibility: same dual-evidence test as the live-recording path.
                    // Accept the partner when EITHER the pre-couple snapshot was
                    // captured (real KSP vessel) OR it has a known Parsek recording.
                    bool partnerSnapshotCapturedR = host.pendingDockPartnerSnapshot != null
                        && host.pendingDockPartnerSnapshotPid == partnerPidFromEventR
                        && partnerPidFromEventR != 0u;
                    bool partnerKnownR = ParsekFlight.IsKnownDockPartnerForRoute(
                        partnerPidFromEventR,
                        host.recorder.RecordingVesselId,
                        host.activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligibleR = partnerPidFromEventR != 0u
                        && partnerPidFromEventR != host.recorder.RecordingVesselId
                        && (partnerSnapshotCapturedR || partnerKnownR);
                    uint routeTargetPid = ParsekFlight.SuppressRouteWindowForEvaGrab(
                        partnerEligibleR ? partnerPidFromEventR : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: " (retroactive)");
                    bool partnerIsEvaR = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEventR);
                    uint partnerStampPidR = ParsekFlight.ResolveBranchPartnerStampPid(
                        partnerPidFromEventR, host.recorder.RecordingVesselId, partnerIsEvaR);
                    string partnerStampGuidR = ParsekFlight.ResolveDockPartnerGuidFromEvent(
                        partnerStampPidR,
                        fromPidR, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPidR, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve (retroactive): " +
                        $"selfPid={host.recorder.RecordingVesselId} mergedPid={mergedPid} " +
                        $"absorbedPid={absorbedPid} partnerPidFromEvent={partnerPidFromEventR} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCapturedR} partnerKnown={partnerKnownR} " +
                        $"partnerEligible={partnerEligibleR} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEvaR} partnerPidStamped={partnerStampPidR} " +
                        $"partnerGuidStamped={partnerStampGuidR ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    host.pendingTreeDockMerge = true;
                    host.pendingDockMergedPid = mergedPid;
                    host.pendingDockAbsorbedPid = absorbedPid;
                    host.pendingDockRouteTargetPid = routeTargetPid;
                    host.pendingDockPartnerPid = partnerStampPidR;
                    host.pendingDockPartnerGuid = partnerStampGuidR;
                    host.pendingDockTransferKind = coupleTransferKind;
                    host.dockConfirmFrames = 0;

                    if (absorbedPid != 0)
                        host.dockingInProgress.Add(absorbedPid);

                    host.Log($"onPartCouple (tree, retroactive): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid})");
                    return;
                }
            }

            // No live tree recorder in a state a dock merge applies to: no tree, no
            // recorder, or a recorder stopped without a capture. Every recording runs
            // inside a tree, so nothing else consumes the couple.
            ParsekLog.Verbose("Flight",
                $"onPartCouple: no dock merge armed (mergedPid={mergedPid}, hasTree={host.activeTree != null}, " +
                $"hasRecorder={host.recorder != null}, recording={host.recorder != null && host.recorder.IsRecording}, " +
                $"hasCapture={host.recorder != null && host.recorder.CaptureAtStop != null})");
        }

        internal void OnPartUndock(Part undockedPart)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (ParsekFlight.restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnPartUndock: skipped — restore coroutine in progress " +
                    $"(part '{undockedPart?.partInfo?.name}')");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnPartUndock:entry", host.CaptureRecorderState());
            ClearPendingUndockSeed();
            if (host.recorder == null || !host.recorder.IsRecording) return;
            if (host.pendingSplitInProgress) return; // another split is already being processed
            if (undockedPart?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnPartUndock: undocked part or vessel is null — ignoring");
                return;
            }

            // onPartUndock fires FIRST inside KSP's Part.Undock(), BEFORE the vessel is
            // split (the part still reports the OLD combined vessel persistentId here, so
            // we deliberately do NOT branch off this pid). KSP creates the new vessel and
            // fires onVesselsUndocking(oldVessel, newVessel) at the END of the same call,
            // where both pids are final. We capture the structural-event snapshot and the
            // departing root part's pre-split origin seed NOW (combined vessel intact, part
            // still attached, the ideal moment) and stash the seed for OnVesselsUndocking,
            // which authors the actual Undock branch (and, via CreateSplitBranch, completes
            // any open logistics route-connection window). See the undock-not-recorded fix.
            double undockEventUT = Planetarium.GetUniversalTime();
            var undockInvolved = new List<Vessel>(1) { undockedPart.vessel };
            host.recorder.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");
            host.backgroundRecorder?.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");

            uint undockedPartPid = undockedPart.persistentId;
            uint undockedRootPartPid = undockedPart.vessel.rootPart?.persistentId ?? 0u;
            if (ParsekFlight.ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                && FlightRecorder.TryCreateAbsoluteTrajectoryPointFromPartOrigin(
                    undockedPart,
                    undockEventUT,
                    out TrajectoryPoint capturedUndockSeed))
            {
                pendingUndockRootPartSeed = capturedUndockSeed;
                ParsekLog.Verbose("Flight",
                    $"Undock root part-origin seed captured: part='{undockedPart.partInfo?.name}' " +
                    $"pid={undockedPartPid} preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"ut={undockEventUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
            else
            {
                string reason = ParsekFlight.ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                    ? "capture-failed"
                    : "undocked-part-not-root";
                ParsekLog.Verbose("Flight",
                    $"Undock part-origin seed skipped: reason={reason} " +
                    $"undockedPartPid={undockedPartPid} rootPartPid={undockedRootPartPid} " +
                    $"preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
        }

        /// <summary>
        /// Authoritative docking-port undock handler. KSP fires
        /// <c>onVesselsUndocking(oldVessel, newVessel)</c> at the end of <c>Part.Undock()</c>
        /// with both vessels fully split and carrying their final persistentIds, so unlike a
        /// stack/radial decoupler (which fires <c>onPartDeCoupleNewVesselComplete</c> and is
        /// caught by the joint-break fallback) we can map the undock directly. This is the
        /// fix for docking-port undocks being mis-recorded as within-segment part loss: the
        /// joint-break fallback can never see the new vessel for an undock, so it classifies
        /// the departing pod as PartDestroyed instead of branching an Undock.
        /// </summary>
        internal void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)
        {
            // Vessel names (design 7.7): the leaving vessel (fresh guid, re-rooted on its
            // pre-dock root part) continues the absorbed identity when a dock-absorb row names
            // its root. Linked whatever the recorder state.
            VesselNameLiveSync.OnUndockSplit(newVessel);

            // #267: skip if restore coroutine is mid-yield; it owns activeTree/recorder.
            if (ParsekFlight.restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnVesselsUndocking: skipped, restore coroutine in progress");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnVesselsUndocking:entry", host.CaptureRecorderState());

            // Snapshot + clear the pre-split seed stashed by OnPartUndock this same frame.
            TrajectoryPoint? undockRootPartSeed = pendingUndockRootPartSeed;
            ClearPendingUndockSeed();

            // A vehicle and a kerbal at the end is an EVA, never an Undock (owner ruling
            // 2026-10-24): stock's seat dismount and claw release of a kerbal reach here
            // through Part.Undock and fire no onCrewOnEva.
            if (oldVessel != null && newVessel != null
                && SegmentBoundaryLogic.ClassifyUndockShape(oldVessel.isEVA, newVessel.isEVA)
                    == UndockShape.KerbalLeftVehicle)
            {
                HandleKerbalLeftVehicleUndock(oldVessel, newVessel);
                return;
            }

            if (host.recorder == null || !host.recorder.IsRecording)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: not recording, ignoring");
                return;
            }
            if (host.pendingSplitInProgress)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: another split is already being processed, ignoring");
                return;
            }
            if (oldVessel == null || newVessel == null)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: null vessel (old={(oldVessel != null ? "set" : "null")}, " +
                    $"new={(newVessel != null ? "set" : "null")}), ignoring");
                return;
            }

            uint recordedPid = host.recorder.RecordingVesselId;
            uint oldPid = oldVessel.persistentId;
            uint newPid = newVessel.persistentId;

            UndockSplitDecision decision =
                SegmentBoundaryLogic.ClassifyUndockSplit(recordedPid, oldPid, newPid);

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: recordedPid={recordedPid} oldPid={oldPid} " +
                $"(name='{oldVessel.vesselName}') newPid={newPid} (name='{newVessel.vesselName}') " +
                $"decision={decision}");

            if (decision == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: undock does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }

            // The recorder maps to KSP's oldVessel (which keeps its persistentId); the new
            // vessel is the freshly split-off side. The deferred branch resolves which side
            // KSP gives focus to and backgrounds the OTHER, so we pass both pids rather than
            // pre-deciding the background here (focus can move to the departing vessel one
            // frame later via WaitAndSwitchFocus when the player kept controlling that pod).

            // The undock event IS the authoritative split signal. Consume the recorder's
            // pending joint-break check (set by the synchronous onPartJointBreak that
            // KSP's attachJoint.DestroyJoint() fired earlier in the same Part.Undock() call)
            // so HandleJointBreakDeferredCheck does NOT also run next frame and misclassify
            // the departing pod as within-segment PartDestroyed. Note: a genuine within-
            // segment break in the SAME physics frame as the undock would also be swallowed
            // here, but the broken part is still hidden on playback via its Decoupled
            // PartEvent (recorded by FlightRecorder.OnPartJointBreak); only the PartDestroyed
            // SegmentEvent label is lost, an acceptable trade for not double-processing.
            bool consumedJointBreak = host.recorder.ConsumePendingJointBreakCheck(out double jointBreakUT);
            if (consumedJointBreak)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "undock owns this split, suppressing within-segment fallback");
            }

            // Tree mode: stop recorder synchronously to prevent OnPhysicsFrame interference,
            // then defer one frame so KSP fully settles the freshly created vessel before we
            // snapshot it (mirrors the proven decouple/EVA deferred-branch pattern).
            host.recorder.StopRecordingForChainBoundary();
            host.pendingSplitRecorder = host.recorder;
            host.recorder = null;
            host.pendingSplitInProgress = true;
            host.Log($"onVesselsUndocking: vessel split off (recordedPid={recordedPid}, " +
                $"oldPid={oldPid}, newPid={newPid}); starting deferred Undock branch");
            host.StartCoroutine(host.DeferredUndockBranch(oldPid, newPid, undockRootPartSeed));
        }

        private void ClearPendingUndockSeed()
        {
            pendingUndockRootPartSeed = null;
        }

        /// <summary>
        /// An undock whose two ends are a vehicle and an EVA kerbal (a kerbal leaving an
        /// external command seat, a claw releasing a kerbal) is recorded as an EVA: the same
        /// deferred EVA branch a hatch EVA takes (kerbal name on the child, no Rewind Point,
        /// the vehicle keeps its own background leg whatever its crew or command state), and
        /// when nothing is recording, the same auto-record. The stock-EVA event shape is
        /// rebuilt from the two vessels (from = the vehicle, to = the kerbal's part); every
        /// consumer reads only the two parts' vessels and the kerbal part's crew.
        /// </summary>
        private void HandleKerbalLeftVehicleUndock(Vessel oldVessel, Vessel newVessel)
        {
            Vessel kerbalVessel = newVessel.isEVA ? newVessel : oldVessel;
            Vessel vehicleVessel = newVessel.isEVA ? oldVessel : newVessel;
            var evaData = new GameEvents.FromToAction<Part, Part>(
                vehicleVessel.rootPart, kerbalVessel.rootPart);
            string kerbalName = ExtractEvaKerbalName(evaData);
            uint recordedPid = host.recorder != null ? host.recorder.RecordingVesselId : 0u;

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: kerbal left vehicle, recording as EVA " +
                $"(kerbal='{kerbalName ?? "<none>"}' kerbalPid={kerbalVessel.persistentId} " +
                $"vehiclePid={vehicleVessel.persistentId} (name='{vehicleVessel.vesselName}') " +
                $"kerbalIsNewVessel={newVessel.isEVA} recording={host.IsRecording} recordedPid={recordedPid})");

            if (!host.IsRecording)
            {
                host.StartCoroutine(host.DeferredKerbalLeftVehicleNotRecording(evaData, kerbalVessel));
                return;
            }
            if (host.pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnVesselsUndocking-EVA",
                    "pendingSplitInProgress",
                    vehicleVessel.persistentId,
                    kerbalVessel.persistentId);
                return;
            }
            if (SegmentBoundaryLogic.ClassifyUndockSplit(
                    recordedPid, oldVessel.persistentId, newVessel.persistentId)
                == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }
            if (string.IsNullOrEmpty(kerbalName))
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle has no crew name " +
                    $"(kerbalPid={kerbalVessel.persistentId}); not branching");
                return;
            }

            // Part.Undock destroyed the attach joint first (synchronous onPartJointBreak); the
            // EVA branch owns this split, so the within-segment fallback must not run too.
            if (host.recorder.ConsumePendingJointBreakCheck(out double jointBreakUT))
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "kerbal-left-vehicle EVA owns this split");
            }

            double evaEventUT = Planetarium.GetUniversalTime();
            var evaInvolved = new List<Vessel>(2) { vehicleVessel, kerbalVessel };
            host.recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");
            host.backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

            host.recorder.StopRecordingForChainBoundary();
            host.pendingSplitRecorder = host.recorder;
            host.recorder = null;
            host.pendingSplitInProgress = true;
            host.Log($"Kerbal left vehicle through an undock: '{kerbalName}' - starting deferred EVA branch");
            host.StartCoroutine(host.DeferredEvaBranch(kerbalName, evaData));
        }

        /// <summary>
        /// Zeros all dock pending-transition fields.
        /// Called from scene change and flight ready.
        /// </summary>
        internal void ClearDockUndockState()
        {
            host.pendingDockMergedPid = 0;
            host.dockConfirmFrames = 0;
            host.pendingDockAbsorbedPid = 0;
            host.pendingDockRouteTargetPid = 0;
            host.pendingDockPartnerPid = 0;
            host.pendingDockPartnerGuid = null;
            host.pendingDockTransferKind = RouteConnectionKind.None;
            host.pendingDockPartnerSnapshot = null;
            host.pendingDockPartnerSnapshotPid = 0u;
            host.pendingDockSelfSnapshot = null;
            host.pendingDockSelfSnapshotPid = 0u;
            host.pendingDockRouteEndpointAtDock = null;
            host.pendingDockRouteEndpointSituation = -1;
        }

        /// <summary>
        /// Handles tree dock merge: creates merge branch point when recorder is stopped
        /// and dock merge is pending. Consumes pending dock state.
        /// </summary>
        internal void HandleTreeDockMerge()
        {
            if (host.activeTree == null || !host.pendingTreeDockMerge || host.recorder == null
                || host.recorder.IsRecording || host.recorder.CaptureAtStop == null)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (host.recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = host.recorder.CaptureAtStop.Points[host.recorder.CaptureAtStop.Points.Count - 1].ut;

            // Determine parent recording IDs
            string activeParentId = host.activeTree.ActiveRecordingId;
            string bgParentId = null;
            // The background partner is whichever vessel (absorbed OR merged) is in BackgroundMap.
            // When we're the TARGET: absorbed vessel is in BackgroundMap.
            // When we're the INITIATOR: merged (surviving) vessel is in BackgroundMap.
            if (host.pendingDockAbsorbedPid != 0)
                host.activeTree.BackgroundMap.TryGetValue(host.pendingDockAbsorbedPid, out bgParentId);
            if (bgParentId == null && host.pendingDockMergedPid != 0)
                host.activeTree.BackgroundMap.TryGetValue(host.pendingDockMergedPid, out bgParentId);

            var stoppedRecorder = host.recorder;
            host.recorder = null;
            // Route target was already resolved from the couple event in OnPartCouple
            // and persisted on pendingDockRouteTargetPid. Falling back to the legacy
            // BackgroundMap-derived ResolveDockRouteTargetPid would drop the partner
            // for cross-tree dock partners (committed-recording case). The ungated
            // partner stamp rides alongside and feeds ONLY the branch point.
            uint routeTargetPid = host.pendingDockRouteTargetPid;
            uint branchPartnerPid = host.pendingDockPartnerPid;
            string branchPartnerGuid = host.pendingDockPartnerGuid;

            CreateMergeBranch(
                BranchPointType.Dock,
                host.pendingDockMergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                routeTargetPid,
                host.pendingDockTransferKind,
                branchPartnerPid,
                branchPartnerGuid);

            // Clean up
            if (host.pendingDockAbsorbedPid != 0)
                host.dockingInProgress.Remove(host.pendingDockAbsorbedPid);

            host.pendingTreeDockMerge = false;
            host.pendingDockMergedPid = 0;
            host.pendingDockAbsorbedPid = 0;
            host.pendingDockRouteTargetPid = 0;
            host.pendingDockPartnerPid = 0;
            host.pendingDockPartnerGuid = null;
            host.pendingDockTransferKind = RouteConnectionKind.None;
            host.pendingDockPartnerSnapshot = null;
            host.pendingDockPartnerSnapshotPid = 0u;
            host.pendingDockSelfSnapshot = null;
            host.pendingDockSelfSnapshotPid = 0u;
            host.pendingDockRouteEndpointAtDock = null;
            host.pendingDockRouteEndpointSituation = -1;
            host.dockConfirmFrames = 0;

            host.Log("Tree dock merge completed");
        }

        /// <summary>
        /// Handles tree board merge: creates merge branch point when boarding is detected
        /// in tree mode and the recorder is stopped with ChainToVesselPending.
        /// </summary>
        internal void HandleTreeBoardMerge()
        {
            if (host.activeTree == null || host.recorder == null || !host.recorder.ChainToVesselPending
                || host.recorder.IsRecording || host.recorder.CaptureAtStop == null
                || host.pendingBoardingTargetPid == 0
                || FlightGlobals.ActiveVessel == null
                || FlightGlobals.ActiveVessel.persistentId != host.pendingBoardingTargetPid)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (host.recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = host.recorder.CaptureAtStop.Points[host.recorder.CaptureAtStop.Points.Count - 1].ut;

            uint mergedPid = host.pendingBoardingTargetPid;

            string activeParentId = host.activeTree.ActiveRecordingId;
            string bgParentId = null;
            if (host.pendingBoardingTargetInTree)
                host.activeTree.BackgroundMap.TryGetValue(mergedPid, out bgParentId);

            string boardPartnerGuid;
            uint boardPartnerPid = ParsekFlight.ResolveBoardPartnerStamp(
                host.pendingBoardingTargetInTree, mergedPid,
                AnchorDetector.TryReadLiveVesselGuid(FlightGlobals.ActiveVessel),
                out boardPartnerGuid);
            ParsekLog.Verbose("Flight",
                $"Board merge partner stamp: targetPid={mergedPid} inTree={host.pendingBoardingTargetInTree} " +
                $"partnerPidStamped={boardPartnerPid} partnerGuidStamped={boardPartnerGuid ?? "(none)"}");

            // Fix 5: clear ChainToVesselPending before CreateMergeBranch
            host.recorder.ChainToVesselPending = false;
            var stoppedRecorder = host.recorder;
            host.recorder = null;

            CreateMergeBranch(
                BranchPointType.Board,
                mergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                branchPartnerPid: boardPartnerPid,
                branchPartnerGuid: boardPartnerGuid);

            host.pendingBoardingTargetPid = 0;
            host.boardingConfirmFrames = 0;
            host.pendingBoardingTargetInTree = false;

            host.Log("Tree board merge completed");
        }

        /// <summary>
        /// An EVA recording stopped on boarding (ChainToVesselPending) that
        /// <see cref="HandleTreeBoardMerge"/> did not consume, because the boarding
        /// confirmation is missing or names another vessel: treat it as a normal stop and
        /// leave CaptureAtStop for the ordinary revert/merge handling.
        /// </summary>
        internal void HandleUnconfirmedBoardingStop()
        {
            if (host.recorder == null || !host.recorder.ChainToVesselPending
                || host.recorder.IsRecording || host.recorder.CaptureAtStop == null)
                return;

            host.recorder.ChainToVesselPending = false;
            host.pendingBoardingTargetPid = 0;
            host.boardingConfirmFrames = 0;
            host.Log("ChainToVessel without boarding confirmation \u2014 treating as normal stop");
            ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
        }

        private static string DescribeDecouplePartForDiagnostics(string label, Part part)
        {
            if (part == null)
                return label + "=null";

            string name = part.partInfo?.name ?? part.name ?? "unknown";
            uint parentPid = part.parent?.persistentId ?? 0u;
            string origin = part.transform != null
                ? DiagnosticFormatters.FormatVector3d(part.transform.position)
                : "no-transform";
            string rotation = part.transform != null
                ? DiagnosticFormatters.FormatQuaternion(part.transform.rotation)
                : "no-transform";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}='{1}' pid={2} parentPid={3} origin={4} rot={5} srfAttach={6}",
                label,
                name,
                part.persistentId,
                parentPid,
                origin,
                rotation,
                DiagnosticFormatters.DescribeSurfaceAttachNode(part));
        }

        private static Part FindFirstDirectChildPart(Part rootPart, IList<Part> parts)
        {
            if (rootPart == null || parts == null)
                return null;

            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                if (part != null && part.parent == rootPart)
                    return part;
            }

            return null;
        }

        internal void HandleJointBreakDeferredCheck()
        {
            if (host.pendingSplitInProgress || host.recorder == null || !host.recorder.IsRecording)
                return;

            double pendingJointBreakUT;
            bool jointBreakPending = host.recorder.ConsumePendingJointBreakCheck(out pendingJointBreakUT);
            bool decoupleCreatedVesselsPending =
                host.pendingDeferredSplitCheckTrigger == ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel
                && host.decoupleCreatedVessels != null
                && host.decoupleCreatedVessels.Count > 0;

            var trigger = ParsekFlight.ResolveDeferredSplitCheckTrigger(
                host.pendingSplitInProgress,
                recorderIsRecording: true,
                jointBreakPending,
                decoupleCreatedVesselsPending);

            if (trigger == ParsekFlight.DeferredSplitCheckTrigger.None)
                return;

            host.pendingDeferredSplitCheckTrigger = trigger;
            host.pendingSplitTriggerUT = jointBreakPending ? pendingJointBreakUT : double.NaN;

            // Snapshot existing vessel PIDs so the deferred check can identify NEW vessels.
            // This only makes sense for true joint-break-triggered checks.
            host.preBreakVesselPids = null;
            if (trigger == ParsekFlight.DeferredSplitCheckTrigger.JointBreak && FlightGlobals.Vessels != null)
            {
                host.preBreakVesselPids = new HashSet<uint>();
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v != null) host.preBreakVesselPids.Add(v.persistentId);
                }
            }
            host.recorder.StopRecordingForChainBoundary();
            host.pendingSplitRecorder = host.recorder;
            host.recorder = null;
            host.pendingSplitInProgress = true;

            if (trigger == ParsekFlight.DeferredSplitCheckTrigger.JointBreak)
                host.Log("Joint break detected \u2014 starting deferred vessel split check");
            else
                host.Log("Decouple-created vessel detected without joint-break callback \u2014 starting deferred vessel split check");
            host.StartCoroutine(host.DeferredJointBreakCheck());
        }

        /// <summary>
        /// Callback for GameEvents.onVesselCreate during the split check window.
        /// Captures vessels immediately at creation, before KSP's debris cleanup can destroy them.
        /// </summary>
        /// <summary>
        /// Callback for GameEvents.onPartDeCoupleNewVesselComplete during active recording.
        /// Fires synchronously inside Part.decouple(), before KSP's debris cleanup can destroy the vessel.
        /// If no structural onPartJointBreak callback reaches the recorder for this split,
        /// arm a decouple-only deferred split check so these captured vessels are processed
        /// promptly instead of lingering until a later unrelated joint break.
        /// </summary>
        internal void OnDecoupleNewVesselDuringSplitCheck(Vessel originalVessel, Vessel newVessel)
        {
            if (newVessel == null) return;
            uint recordedVesselPid = host.recorder?.RecordingVesselId
                ?? host.pendingSplitRecorder?.RecordingVesselId
                ?? 0;
            uint originalVesselPid = originalVessel?.persistentId ?? 0;
            if (originalVesselPid != 0u)
                host.decoupleOriginByNewPid[newVessel.persistentId] = originalVesselPid;
            if (!ParsekFlight.ShouldCaptureDecoupleCreatedVessel(recordedVesselPid, originalVesselPid))
            {
                ParsekLog.VerboseRateLimited("Flight",
                    $"decouple-created-foreign-{originalVesselPid}-{newVessel.persistentId}",
                    $"Ignoring decouple-created vessel pid={newVessel.persistentId}: " +
                    $"originalPid={originalVesselPid}, recordedPid={recordedVesselPid}");
                return;
            }
            // Only capture vessels that didn't exist before the break
            if (host.preBreakVesselPids != null && host.preBreakVesselPids.Contains(newVessel.persistentId))
                return;
            host.decoupleCreatedVessels?.Add(newVessel);
            if (!host.pendingSplitInProgress && host.preBreakVesselPids == null)
                host.pendingDeferredSplitCheckTrigger = ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel;
            // Capture controller status NOW — vessel parts may be cleared by the deferred check.
            // A crewed vessel with no command module counts: it records as its own branch.
            bool hasController = ParsekFlight.ShouldRecordAsOwnBranch(newVessel);
            if (host.decoupleControllerStatus != null)
                host.decoupleControllerStatus[newVessel.persistentId] = hasController;
            double splitUT = Planetarium.GetUniversalTime();
            uint rootPartPersistentId = newVessel.rootPart?.persistentId ?? 0u;
            string seedSource = "none";
            string pendingJointChildSeedPids = "none";
            bool rootMatchesPendingJointChildSeed = false;
            if (host.decoupleCreatedTrajectoryPoints != null
                && !host.decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                FlightRecorder seedRecorder = host.recorder ?? host.pendingSplitRecorder;
                ParsekFlight.TryResolveTrajectoryPoint resolvePartOriginSeed = null;
                if (seedRecorder != null)
                {
                    rootMatchesPendingJointChildSeed =
                        seedRecorder.ContainsPendingJointChildPartOriginSeed(rootPartPersistentId);
                    resolvePartOriginSeed = seedRecorder.TryConsumePendingJointChildPartOriginSeed;
                    pendingJointChildSeedPids =
                        seedRecorder.DescribePendingJointChildPartOriginSeedIdsForDiagnostics();
                }
                // On stock decoupler splits, KSP roots the new debris vessel at
                // joint.Child; if it chooses a different root, this misses cleanly and
                // falls back to the post-split root-part sample below.
                if (ParsekFlight.TrySelectDecouplePartOriginSeed(
                        rootPartPersistentId,
                        resolvePartOriginSeed,
                        out TrajectoryPoint partOriginSeed))
                {
                    host.decoupleCreatedTrajectoryPoints[newVessel.persistentId] = partOriginSeed;
                    seedSource = "joint-child-part-origin";
                }
                else
                {
                    host.decoupleCreatedTrajectoryPoints[newVessel.persistentId] =
                        BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                            newVessel, splitUT, preferRootPartSurfacePose: true);
                    seedSource = "new-vessel-root-part";
                }
                // ---- Trace-Sep: log seed source + new-vessel state at split ----
                if (TraceSeparation.RecordingWindowActive)
                {
                    Vessel original = originalVessel;
                    CelestialBody body = newVessel.mainBody ?? original?.mainBody;
                    Vector3d newLlaWorld = body != null
                        ? body.GetWorldSurfacePosition(newVessel.latitude, newVessel.longitude, newVessel.altitude)
                        : Vector3d.zero;
                    Vector3d newTransformPos = newVessel.transform != null
                        ? (Vector3d)newVessel.transform.position : Vector3d.zero;
                    Vector3d originalLlaWorld = body != null && original != null
                        ? body.GetWorldSurfacePosition(original.latitude, original.longitude, original.altitude)
                        : Vector3d.zero;
                    Vector3 newSrfVel = newVessel.srf_velocity;
                    Vector3 originalSrfVel = original != null ? original.srf_velocity : Vector3.zero;
                    float dt = Time.fixedDeltaTime;
                    Vector3d newWorldDelta = newTransformPos - newLlaWorld;
                    TraceSeparation.RecordLog("DecoupleSeed",
                        "newPid=" + newVessel.persistentId +
                        " name='" + newVessel.vesselName + "'" +
                        " seedSource=" + seedSource +
                        " splitUT=" + splitUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " newLLA=(" + newVessel.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" +
                        " newLLAWorld=" + TraceSeparation.FormatVector3d(newLlaWorld) +
                        " newTransformPos=" + TraceSeparation.FormatVector3d(newTransformPos) +
                        " newTransformVsLLA=" + TraceSeparation.FormatVector3d(newWorldDelta) +
                        " |newDelta|=" + newWorldDelta.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalLLAWorld=" + TraceSeparation.FormatVector3d(originalLlaWorld) +
                        " newVsOriginalLLA=" + TraceSeparation.FormatVector3d(newLlaWorld - originalLlaWorld) +
                        " newSrfVel=" + TraceSeparation.FormatVector3(newSrfVel) + " |vs|=" + newSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " predictedNewSrfStep=" + (newSrfVel.magnitude * dt).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalSrfVel=" + TraceSeparation.FormatVector3(originalSrfVel) + " |ovs|=" + originalSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
                // ---- /Trace-Sep ----
            }
            else if (host.decoupleCreatedTrajectoryPoints != null
                     && host.decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                seedSource = "already-captured";
            }
            ParsekLog.Info("Flight",
                $"Decouple created vessel during recording: pid={newVessel.persistentId} " +
                $"name='{newVessel.vesselName}' type={newVessel.vesselType} " +
                $"parts={newVessel.parts?.Count ?? 0} hasController={hasController} " +
                $"rootPartPid={rootPartPersistentId} seedSource={seedSource} " +
                $"pendingJointChildSeedPids={pendingJointChildSeedPids} " +
                $"splitUT={splitUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"capturedSeed={(host.decoupleCreatedTrajectoryPoints != null && host.decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId) ? "T" : "F")}");
            Part rootPart = newVessel.rootPart;
            ParsekLog.Verbose("Flight",
                $"Decouple created vessel diagnostics: pid={newVessel.persistentId} " +
                $"rootMatchesPendingJointChildSeed={(rootMatchesPendingJointChildSeed ? "T" : "F")} " +
                $"{DescribeDecouplePartForDiagnostics("root", rootPart)} " +
                $"{DescribeDecouplePartForDiagnostics("firstChild", FindFirstDirectChildPart(rootPart, newVessel.parts))}");
        }
    }
}
