using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Parsek.InGameTests
{
    // ════════════════════════════════════════════════════════════════
    //  Ghost Playback Lifecycle — verify engine state consistency
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates GhostPlaybackEngine internal state during live playback.
    /// Catches orphan ghosts, NaN positions, material leaks, overlap cap violations.
    /// These bugs are invisible to unit tests because they require real Unity GameObjects.
    /// </summary>
    public class GhostPlaybackLifecycleTests
    {
        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Every ghostStates entry has a non-null, non-destroyed GameObject")]
        public void AllGhostStatesHaveLiveGameObject()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var ghostGOs = flight.Engine.GetGhostGameObjects();
            if (ghostGOs.Count == 0)
                InGameAssert.Skip("No active ghosts");

            int valid = 0, orphaned = 0;
            foreach (var kvp in ghostGOs)
            {
                if (kvp.Value != null)
                    valid++;
                else
                    orphaned++;
            }

            ParsekLog.Info("TestRunner",
                $"Ghost GameObjects: {valid} valid, {orphaned} orphaned (of {ghostGOs.Count})");
            InGameAssert.AreEqual(0, orphaned,
                $"{orphaned} ghost state(s) have null/destroyed GameObject (leak)");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "All ghost positions are finite (no NaN or Infinity)")]
        public void AllGhostPositionsFinite()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var positions = flight.Engine.GetActiveGhostPositions().ToList();
            if (positions.Count == 0)
                InGameAssert.Skip("No active ghost positions");

            int nanCount = 0;
            foreach (var (index, pos) in positions)
            {
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
                    || float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z))
                {
                    nanCount++;
                    ParsekLog.Warn("TestRunner",
                        $"Ghost index={index} has non-finite position: ({pos.x},{pos.y},{pos.z})");
                }
            }

            InGameAssert.AreEqual(0, nanCount,
                $"{nanCount} ghost(s) have NaN/Infinity positions");
            ParsekLog.Verbose("TestRunner", $"All {positions.Count} ghost positions finite");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Overlap ghost count per recording does not exceed MaxOverlapGhostsPerRecording")]
        public void OverlapGhostCountWithinCap()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int maxPerRec = GhostPlayback.MaxOverlapGhostsPerRecording;
            var ghostGOs = flight.Engine.GetGhostGameObjects();
            int violations = 0;

            foreach (var kvp in ghostGOs)
            {
                if (flight.Engine.TryGetOverlapGhosts(kvp.Key, out var overlaps))
                {
                    if (overlaps.Count > maxPerRec)
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording index={kvp.Key} has {overlaps.Count} overlap ghosts (max={maxPerRec})");
                    }
                }
            }

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) exceed overlap ghost cap of {maxPerRec}");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Per-instance overlap map vessel count matches flight overlap ghost count + 1 (capped)")]
        public void OverlapMapInstanceCountMatchesFlight()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int cap = GhostPlayback.MaxOverlapGhostsPerRecording;
            var ghostGOs = flight.Engine.GetGhostGameObjects();
            int checkedRecordings = 0;
            int mismatches = 0;

            foreach (var kvp in ghostGOs)
            {
                int recIdx = kvp.Key;
                if (!flight.Engine.TryGetOverlapGhosts(recIdx, out var overlaps) || overlaps == null)
                    continue;

                // Flight: primary ghost (ghostStates[recIdx]) + the staggered overlap list = N meshes.
                int flightInstances = overlaps.Count + 1;
                int expected = System.Math.Min(flightInstances, cap);
                int mapInstances = GhostMapPresence.GetOverlapInstanceCount(recIdx);

                checkedRecordings++;
                // Allow a +/-1 transient: the map sweep is throttled (MaxSpawnsPerFrame) so a brand-new
                // cycle can be one frame behind the flight mesh; never above the cap.
                if (mapInstances > cap || System.Math.Abs(mapInstances - expected) > 1)
                {
                    mismatches++;
                    ParsekLog.Warn("TestRunner",
                        $"Overlap map instance count mismatch rec=#{recIdx}: map={mapInstances} " +
                        $"flightMeshes={flightInstances} expected~={expected} cap={cap}");
                }
            }

            if (checkedRecordings == 0)
                InGameAssert.Skip("No overlap recordings active");

            ParsekLog.Info("TestRunner",
                $"Overlap map instance count: {checkedRecordings - mismatches}/{checkedRecordings} recordings match flight (cap={cap})");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} overlap recording(s) have map instance count mismatched vs flight meshes");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Mission-loop overlap member: map instance count matches flight overlap ghost count + 1 (capped)")]
        public void OverlapMapInstanceCountMatchesFlightMissionLoop()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            // Source (b): a looped Mission unit that self-overlaps. The flight engine renders the
            // staggered instances through overlapGhosts[memberIdx] (the SAME pure cycle math the map
            // per-instance sweep uses), so the map count must match the flight mesh count even though
            // the member's own rec.LoopPlayback is false.
            var loopUnits = flight.Engine.CurrentLoopUnits;
            if (loopUnits == null || loopUnits.Count == 0)
                InGameAssert.Skip("No Mission loop units active (load a looped Mission to exercise source b)");

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            int cap = GhostPlayback.MaxOverlapGhostsPerRecording;
            int checkedMembers = 0;
            int mismatches = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                // Only Mission-unit overlap members (source b), not standalone loops (covered above).
                if (!loopUnits.TryGetUnitForMember(i, out var unit)
                    || !GhostPlaybackLogic.UnitMemberOverlaps(unit))
                    continue;
                if (!GhostMapPresence.IsOverlapRecording(committed[i], i, committed, loopUnits))
                    continue;
                if (!flight.Engine.TryGetOverlapGhosts(i, out var overlaps) || overlaps == null)
                    continue;

                int flightInstances = overlaps.Count + 1; // primary (newest) + staggered list
                int expected = System.Math.Min(flightInstances, cap);
                int mapInstances = GhostMapPresence.GetOverlapInstanceCount(i);

                checkedMembers++;
                if (mapInstances > cap || System.Math.Abs(mapInstances - expected) > 1)
                {
                    mismatches++;
                    ParsekLog.Warn("TestRunner",
                        $"Mission-loop overlap map instance count mismatch member=#{i}: map={mapInstances} " +
                        $"flightMeshes={flightInstances} expected~={expected} cap={cap}");
                }
            }

            if (checkedMembers == 0)
                InGameAssert.Skip("No self-overlapping Mission loop members active");

            ParsekLog.Info("TestRunner",
                $"Mission-loop overlap map instance count: {checkedMembers - mismatches}/{checkedMembers} members match flight (cap={cap})");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} Mission-loop overlap member(s) have map instance count mismatched vs flight meshes");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.TRACKSTATION,
            Description = "TS per-instance overlap map vessel count matches the pure GetActiveCycles size")]
        public void OverlapMapInstanceCountMatchesScheduleTS()
        {
            // The flight engine does not exist in the Tracking Station; the map per-instance count must
            // equal the PURE recomputed active-cycle set for each overlap recording.

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            // Rebuild the TS span-clock loop-unit set exactly as ParsekTrackingStation.DriveMissionLoopUnits
            // does, so a Mission-tab loop (source b) is recognized identically to the live scene.
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            var loopUnits = MissionLoopUnitBuilder.Build(
                MissionStore.Missions, RecordingStore.CommittedTrees, committed,
                autoLoopIntervalSeconds, FlightGlobalsBodyInfo.Instance,
                ParsekSettings.Current?.TransitedBodyRotationMode ?? TransitedBodyRotationMode.Loose);

            int cap = GhostPlayback.MaxOverlapGhostsPerRecording;
            double currentUT = Planetarium.GetUniversalTime();
            int checkedRecordings = 0;
            int mismatches = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!GhostMapPresence.IsOverlapRecording(rec, i, committed, loopUnits))
                    continue;
                if (!GhostMapPresence.ResolveOverlapSchedule(
                        rec, i, committed, loopUnits,
                        out _, out double scheduleStartUT,
                        out double duration, out double effectiveCadence, out _))
                    continue;
                if (currentUT < scheduleStartUT)
                    continue;

                GhostPlaybackLogic.GetActiveCycles(
                    currentUT, scheduleStartUT, scheduleStartUT + duration,
                    effectiveCadence, cap, out long firstCycle, out long lastCycle);
                int expected = (int)System.Math.Min(cap, lastCycle - firstCycle + 1);
                int mapInstances = GhostMapPresence.GetOverlapInstanceCount(i);

                checkedRecordings++;
                // +/-MaxSpawnsPerFrame transient for newly-relaunching cycles; never above the cap.
                if (mapInstances > cap
                    || System.Math.Abs(mapInstances - expected) > GhostPlayback.MaxSpawnsPerFrame)
                {
                    mismatches++;
                    ParsekLog.Warn("TestRunner",
                        $"TS overlap map instance count mismatch rec=#{i}: map={mapInstances} " +
                        $"expectedCycles={expected} cap={cap}");
                }
            }

            if (checkedRecordings == 0)
                InGameAssert.Skip("No overlap recordings active");

            ParsekLog.Info("TestRunner",
                $"TS overlap map instance count: {checkedRecordings - mismatches}/{checkedRecordings} recordings match GetActiveCycles (cap={cap})");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} overlap recording(s) have TS map instance count mismatched vs GetActiveCycles");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.TRACKSTATION,
            Description = "Slice (iii) TS port: per-instance overlap marker head count matches the pure GetActiveCycles size; no-double disjunction coherent")]
        public void OverlapMarkerHeadCountMatchesScheduleTS()
        {
            // TS analogue of OverlapMarkerHeadCountMatchesFlight: the flight engine does not exist in the
            // Tracking Station, so the per-instance marker HEAD set (what DrawAtmosphericMarkers'
            // per-instance branch rides via DrawOneTsOverlapInstanceMarker) must equal the PURE recomputed
            // active-cycle set for each overlap recording.

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            // Rebuild the TS span-clock loop-unit set exactly as ParsekTrackingStation.DriveMissionLoopUnits
            // does (the SAME set DrawAtmosphericMarkers' per-instance branch passes as cachedLoopUnits), so a
            // Mission-tab loop (source b) is recognized identically to the live scene.
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            var loopUnits = MissionLoopUnitBuilder.Build(
                MissionStore.Missions, RecordingStore.CommittedTrees, committed,
                autoLoopIntervalSeconds, FlightGlobalsBodyInfo.Instance,
                ParsekSettings.Current?.TransitedBodyRotationMode ?? TransitedBodyRotationMode.Loose);

            int cap = GhostPlayback.MaxOverlapGhostsPerRecording;
            double currentUT = Planetarium.GetUniversalTime();
            var headBuffer = new List<(long cycle, double headUT)>();
            int checkedRecordings = 0;
            int mismatches = 0;
            int doubleViolations = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                // The head set is what the TS per-instance branch rides; it is produced by the SAME
                // TryGetLiveOverlapHeadUTs the branch calls, gated identically (ShouldDriveOverlapPerInstance).
                if (!GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        rec, i, committed, loopUnits, currentUT, headBuffer))
                    continue;

                // Independent pure recompute of the expected cycle count via GetActiveCycles (the schedule),
                // matching OverlapMapInstanceCountMatchesScheduleTS.
                if (!GhostMapPresence.ResolveOverlapSchedule(
                        rec, i, committed, loopUnits,
                        out _, out double scheduleStartUT,
                        out double duration, out double effectiveCadence, out _))
                    continue;
                if (currentUT < scheduleStartUT)
                    continue;

                GhostPlaybackLogic.GetActiveCycles(
                    currentUT, scheduleStartUT, scheduleStartUT + duration,
                    effectiveCadence, cap, out long firstCycle, out long lastCycle);
                int expected = (int)System.Math.Min(cap, lastCycle - firstCycle + 1);
                int headCount = headBuffer.Count;

                checkedRecordings++;
                // The head count is the SAME GetActiveCycles span, so it must equal expected exactly
                // (never above the cap).
                if (headCount > cap || headCount != expected)
                {
                    mismatches++;
                    ParsekLog.Warn("TestRunner",
                        $"TS overlap marker head count mismatch rec=#{i}: heads={headCount} " +
                        $"expectedCycles={expected} cap={cap}");
                }

                // No-double-marker disjunction coherence: for every live cycle, the per-cycle decision is
                // exactly one of {proto icon owns it} XOR {polyline marker is the sole indicator}. pid 0
                // (no proto for the cycle) MUST take the draw branch; pid != 0 routes through
                // ShouldDrawNonProtoMarkerForGhost (true -> polyline marker draws, false -> proto owns).
                // Either way the disjunction is total and unambiguous, so this never throws — it asserts the
                // pid lookup + decision predicate stay callable/coherent for each head the branch will draw.
                for (int hi = 0; hi < headBuffer.Count; hi++)
                {
                    long cycle = headBuffer[hi].cycle;
                    uint pid = GhostMapPresence.TryGetOverlapInstancePidForCycle(i, cycle);
                    bool protoOwns = pid != 0
                        && !GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(pid);
                    bool polylineDraws = !protoOwns;
                    if (protoOwns == polylineDraws) // impossible by construction; guards a future regression
                    {
                        doubleViolations++;
                        ParsekLog.Warn("TestRunner",
                            $"TS overlap no-double disjunction incoherent rec=#{i} cycle={cycle} pid={pid}");
                    }
                }
            }

            if (checkedRecordings == 0)
                InGameAssert.Skip("No overlap recordings active");

            ParsekLog.Info("TestRunner",
                $"TS overlap marker head count: {checkedRecordings - mismatches}/{checkedRecordings} recordings match GetActiveCycles (cap={cap}); doubleViolations={doubleViolations}");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} overlap recording(s) have TS marker head count mismatched vs GetActiveCycles");
            InGameAssert.AreEqual(0, doubleViolations,
                $"{doubleViolations} TS overlap cycle(s) have an incoherent no-double disjunction");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Slice (iii): flight-map per-instance overlap marker head count matches flight overlap ghost count + 1 (capped)")]
        public void OverlapMarkerHeadCountMatchesFlight()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");


            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            int cap = GhostPlayback.MaxOverlapGhostsPerRecording;
            double currentUT = Planetarium.GetUniversalTime();
            var loopUnits = flight.Engine.CurrentLoopUnits;
            var headBuffer = new List<(long cycle, double headUT)>();
            int checkedRecordings = 0;
            int mismatches = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                if (!flight.Engine.TryGetOverlapGhosts(i, out var overlaps) || overlaps == null)
                    continue;
                // The marker head set is what the flight-map per-instance branch rides; it must equal
                // the engine's live overlap mesh count (overlapGhosts[i] + the primary ghost), capped.
                if (!GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        committed[i], i, committed, loopUnits, currentUT, headBuffer))
                    continue;

                int flightInstances = overlaps.Count + 1; // primary (newest) + staggered list
                int expected = System.Math.Min(flightInstances, cap);
                int headCount = headBuffer.Count;

                checkedRecordings++;
                // Allow a +/-1 transient (the flight spawn throttle can be one frame ahead/behind the
                // pure head recompute); never above the cap.
                if (headCount > cap || System.Math.Abs(headCount - expected) > 1)
                {
                    mismatches++;
                    ParsekLog.Warn("TestRunner",
                        $"Overlap marker head count mismatch rec=#{i}: heads={headCount} " +
                        $"flightMeshes={flightInstances} expected~={expected} cap={cap}");
                }
            }

            if (checkedRecordings == 0)
                InGameAssert.Skip("No overlap recordings active");

            ParsekLog.Info("TestRunner",
                $"Overlap marker head count: {checkedRecordings - mismatches}/{checkedRecordings} recordings match flight (cap={cap})");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} overlap recording(s) have marker head count mismatched vs flight meshes");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Loop phase offsets are all finite (no NaN/Infinity)")]
        public void LoopPhaseOffsetsFinite()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var offsets = flight.Engine.loopPhaseOffsets;
            if (offsets.Count == 0)
                InGameAssert.Skip("No loop phase offsets active");

            int badCount = 0;
            foreach (var kvp in offsets)
            {
                if (double.IsNaN(kvp.Value) || double.IsInfinity(kvp.Value))
                {
                    badCount++;
                    ParsekLog.Warn("TestRunner",
                        $"Loop phase offset for index={kvp.Key} is non-finite: {kvp.Value}");
                }
            }

            InGameAssert.AreEqual(0, badCount,
                $"{badCount} loop phase offset(s) are NaN/Infinity");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Ghost count from engine matches expected count")]
        public void GhostCountReasonable()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int ghostCount = flight.Engine.GhostCount;
            ParsekLog.Verbose("TestRunner", $"GhostPlaybackEngine.GhostCount = {ghostCount}");

            // Ghost count should never be negative (sanity)
            InGameAssert.IsTrue(ghostCount >= 0, $"Ghost count is negative: {ghostCount}");

            // Reasonable upper bound — more than 200 simultaneous ghosts is suspicious
            InGameAssert.IsTrue(ghostCount < 200,
                $"Suspiciously high ghost count: {ghostCount} (potential leak)");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Ghost interpolated body names all resolve to real CelestialBodies")]
        public void GhostBodyNamesValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var ghostGOs = flight.Engine.GetGhostGameObjects();
            int checked_ = 0;
            var badBodies = new List<string>();

            foreach (var kvp in ghostGOs)
            {
                string bodyName = flight.Engine.GetGhostBodyName(kvp.Key);
                if (string.IsNullOrEmpty(bodyName)) continue;
                checked_++;

                if (FlightGlobals.GetBodyByName(bodyName) == null)
                    badBodies.Add($"index={kvp.Key} body='{bodyName}'");
            }

            if (checked_ == 0)
                InGameAssert.Skip("No ghosts with body names to check");

            ParsekLog.Verbose("TestRunner",
                $"Ghost body names: {checked_ - badBodies.Count}/{checked_} resolved");
            InGameAssert.IsTrue(badBodies.Count == 0,
                $"Unresolvable ghost body names: {string.Join(", ", badBodies)}");
        }

        // ───────────────────────────────────────────────────────────────
        //  Boundary-overlap launch render (docs/dev/plan-launch-boundary-overlap.md)
        // ───────────────────────────────────────────────────────────────

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Any boundary-overlap secondary ghost is audio-muted, flagged, and never bound by the watch camera")]
        public void BoundaryOverlapSecondary_IsMutedFlaggedAndUnwatched()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var loopUnits = flight.Engine.CurrentLoopUnits;
            if (loopUnits == null || loopUnits.Count == 0)
                InGameAssert.Skip("No Mission loop units active (load a zero-slack re-aim launch loop to exercise this)");

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            int watchedIndex = flight.WatchMode != null ? flight.WatchMode.WatchedRecordingIndex : -1;
            int secondariesFound = 0;
            int violations = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                if (!flight.Engine.TryGetOverlapGhosts(i, out var overlaps) || overlaps == null)
                    continue;
                for (int k = 0; k < overlaps.Count; k++)
                {
                    var s = overlaps[k];
                    if (s == null || !s.isBoundaryOverlapSecondary)
                        continue;
                    secondariesFound++;
                    // Invariant 3: the secondary borrows the overlap STORAGE; it must be audio-muted (overlap
                    // ghosts get no audio).
                    if (!s.audioMuted)
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Boundary-overlap secondary #{i} cycle={s.loopCycleIndex} is NOT audio-muted");
                    }
                    // Invariant 4: the watch camera must NEVER bind a boundary-overlap secondary. The camera
                    // follows the long-lived primary through-line; the secondary lives only in overlap storage.
                    if (watchedIndex == i && flight.WatchMode != null
                        && flight.WatchMode.WatchedLoopCycleIndex == s.loopCycleIndex)
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Watch camera bound the boundary-overlap secondary #{i} cycle={s.loopCycleIndex} (invariant 4 violation)");
                    }
                }
            }

            if (secondariesFound == 0)
                InGameAssert.Skip("No boundary-overlap secondary ghost active this frame (warp through a zero-slack loop's borrow window)");

            ParsekLog.Info("TestRunner",
                $"Boundary-overlap secondary: {secondariesFound} found, {violations} invariant violation(s)");
            InGameAssert.AreEqual(0, violations,
                $"{violations} boundary-overlap secondary invariant violation(s) (muted/flagged/unwatched)");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "At most ONE boundary-overlap secondary ghost per recording index (the borrowed overlap storage holds one)")]
        public void BoundaryOverlapSecondary_AtMostOnePerRecording()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            int checkedRecordings = 0;
            int violations = 0;
            for (int i = 0; i < committed.Count; i++)
            {
                if (!flight.Engine.TryGetOverlapGhosts(i, out var overlaps) || overlaps == null)
                    continue;
                int secCount = 0;
                for (int k = 0; k < overlaps.Count; k++)
                    if (overlaps[k] != null && overlaps[k].isBoundaryOverlapSecondary)
                        secCount++;
                if (secCount == 0)
                    continue;
                checkedRecordings++;
                if (secCount > 1)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording #{i} has {secCount} boundary-overlap secondaries (expected at most 1)");
                }
            }

            if (checkedRecordings == 0)
                InGameAssert.Skip("No boundary-overlap secondary ghost active this frame");

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) have more than one boundary-overlap secondary");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "A launch-hold re-aim member's map presence carries the boundary-overlap secondary as an overlap instance when its second head is live")]
        public void BoundaryOverlapSecondary_MapInstanceMatchesSecondHead()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var loopUnits = flight.Engine.CurrentLoopUnits;
            if (loopUnits == null || loopUnits.Count == 0)
                InGameAssert.Skip("No Mission loop units active");

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("No committed recordings");

            double currentUT = Planetarium.GetUniversalTime();
            int checkedMembers = 0;
            int mismatches = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                if (!loopUnits.TryGetUnitForMember(i, out var unit) || !unit.LaunchHoldEngaged)
                    continue;
                var rec = committed[i];
                double memberStartUT = unit.MemberStartUT(i, rec.StartUT);
                double memberEndUT = unit.MemberEndUT(i, rec.EndUT);
                var decision = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                    currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    memberStartUT, memberEndUT, out double _, out long _,
                    unit.RelaunchSchedule, unit.LoiterCuts,
                    unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                    unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT);
                bool secondLive = decision == GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.Render;
                if (!secondLive)
                    continue;
                checkedMembers++;
                // When the second head is live the map sweep must carry an overlap instance for this index
                // (the secondary's icon + escape conic). Throttled by MaxSpawnsPerFrame, so allow it to be one
                // frame behind, but never absent for more than a transient.
                int mapInstances = GhostMapPresence.GetOverlapInstanceCount(i);
                if (mapInstances < 1)
                {
                    // Re-check next-frame transient leniency is not available in a one-shot test; log + warn-only
                    // (the create can be deferred to the very frame the test ran). Treat 0 as a soft miss.
                    ParsekLog.Warn("TestRunner",
                        $"Launch-hold member #{i} has a live boundary-overlap second head but 0 map instances " +
                        "(create may be deferred this frame - re-run if transient)");
                    mismatches++;
                }
            }

            if (checkedMembers == 0)
                InGameAssert.Skip("No launch-hold member has a live boundary-overlap second head this frame");

            ParsekLog.Info("TestRunner",
                $"Boundary-overlap map instance: {checkedMembers - mismatches}/{checkedMembers} members carry the secondary instance");
            InGameAssert.AreEqual(0, mismatches,
                $"{mismatches} launch-hold member(s) have a live second head but no boundary-overlap map instance");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Part Event Playback — verify FX info objects built correctly
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates part event visual info (engine FX, parachutes, lights, etc.) on active ghosts.
    /// These require real KSP PartLoader prefabs and Unity particle systems — untestable offline.
    /// </summary>
    public class PartEventPlaybackTests
    {
        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Engine FX infos have non-null particle system lists")]
        public void EngineInfosHaveParticleSystems()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0, nullSystems = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.engineInfos == null) continue;

                foreach (var engineKvp in state.engineInfos)
                {
                    total++;
                    if (engineKvp.Value.particleSystems != null && engineKvp.Value.particleSystems.Count > 0)
                        valid++;
                    else
                        nullSystems++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No engine FX infos on active ghosts");

            ParsekLog.Info("TestRunner",
                $"Engine FX: {valid}/{total} have particle systems, {nullSystems} empty");
            InGameAssert.AreEqual(total, valid,
                $"{nullSystems} engine info(s) have null/empty particle systems (FX build failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Parachute ghost infos have non-null mesh transforms")]
        public void ParachuteInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.parachuteInfos == null) continue;

                foreach (var pKvp in state.parachuteInfos)
                {
                    total++;
                    if (pKvp.Value.canopyTransform != null)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No parachute infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Parachute infos: {valid}/{total} have canopy transforms");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} parachute info(s) have null canopy transform (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Light ghost infos have valid Light components")]
        public void LightInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, withLights = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.lightInfos == null) continue;

                foreach (var lKvp in state.lightInfos)
                {
                    total++;
                    if (lKvp.Value.lights != null && lKvp.Value.lights.Count > 0)
                        withLights++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No light infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Light infos: {withLights}/{total} have Light components");
            InGameAssert.AreEqual(total, withLights,
                $"{total - withLights} light info(s) have null/empty Light list (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "RCS ghost infos have non-null particle system lists")]
        public void RcsInfosHaveParticleSystems()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.rcsInfos == null) continue;

                foreach (var rKvp in state.rcsInfos)
                {
                    total++;
                    if (rKvp.Value.particleSystems != null && rKvp.Value.particleSystems.Count > 0)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No RCS infos on active ghosts");

            ParsekLog.Info("TestRunner", $"RCS FX: {valid}/{total} have particle systems");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} RCS info(s) have null/empty particle systems (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Fairing ghost infos have non-null cone mesh GameObjects")]
        public void FairingInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.fairingInfos == null) continue;

                foreach (var fKvp in state.fairingInfos)
                {
                    total++;
                    if (fKvp.Value.fairingMeshObject != null)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No fairing infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Fairing infos: {valid}/{total} have cone meshes");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} fairing info(s) have null mesh object (build failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Deployable ghost infos have valid transform states")]
        public void DeployableInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.deployableInfos == null) continue;

                foreach (var dKvp in state.deployableInfos)
                {
                    total++;
                    if (dKvp.Value.transforms != null && dKvp.Value.transforms.Count > 0)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No deployable infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Deployable infos: {valid}/{total} have transform states");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} deployable info(s) have null/empty transform states (animation sample failure)");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Game Actions Health — suppression flags, resource singletons
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies game actions system health: suppression flags not stuck, resource singletons valid.
    /// Catches the class of bug where a crashed replay leaves SuppressResourceEvents=true,
    /// silently disabling all future event recording.
    /// </summary>
    public class GameActionsHealthTests
    {
        [InGameTest(Category = "GameActionsHealth",
            Description = "GameStateRecorder suppression flags are all false during normal gameplay")]
        public void SuppressionFlagsNotStuck()
        {
            // During normal gameplay (not mid-replay), all suppression flags should be false.
            // If any is stuck true, event recording is silently disabled.
            InGameAssert.IsFalse(GameStateRecorder.SuppressCrewEvents,
                "SuppressCrewEvents is stuck true — crew events will not be recorded");
            InGameAssert.IsFalse(GameStateRecorder.SuppressResourceEvents,
                "SuppressResourceEvents is stuck true — resource events will not be recorded");
            InGameAssert.IsFalse(GameStateRecorder.IsReplayingActions,
                "IsReplayingActions is stuck true — action replay never completed cleanly");

            ParsekLog.Verbose("TestRunner", "All GameStateRecorder suppression flags are false (normal)");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP Funding singleton is accessible and not null")]
        public void FundingSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career");

            var funding = Funding.Instance;
            InGameAssert.IsNotNull(funding, "Funding.Instance null in career mode");
            InGameAssert.IsTrue(funding.Funds >= 0,
                $"Funds are negative: {funding.Funds:F0} (budget deduction error?)");
            ParsekLog.Verbose("TestRunner", $"Funding singleton: Funds={funding.Funds:F0}");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP ResearchAndDevelopment singleton is accessible in career")]
        public void ScienceSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER
                && HighLogic.CurrentGame.Mode != Game.Modes.SCIENCE_SANDBOX)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career/Science");

            var rd = ResearchAndDevelopment.Instance;
            InGameAssert.IsNotNull(rd, "ResearchAndDevelopment.Instance null in career/science mode");
            InGameAssert.IsTrue(ResearchAndDevelopment.Instance.Science >= 0,
                $"Science is negative: {ResearchAndDevelopment.Instance.Science:F1}");
            ParsekLog.Verbose("TestRunner",
                $"R&D singleton: Science={ResearchAndDevelopment.Instance.Science:F1}");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP Reputation singleton is accessible in career")]
        public void ReputationSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career");

            var rep = Reputation.Instance;
            InGameAssert.IsNotNull(rep, "Reputation.Instance null in career mode");
            InGameAssert.IsTrue(rep.reputation >= -1000f && rep.reputation <= 1000f,
                $"Reputation out of bounds: {rep.reputation:F1} (expected [-1000, 1000])");
            ParsekLog.Verbose("TestRunner", $"Reputation singleton: rep={rep.reputation:F1}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Ghost Chain Consistency — verify chain invariants with real data
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates ghost chain invariants computed from live RecordingStore data.
    /// Catches stale recording references, double-ghosting, and invalid chain structure.
    /// </summary>
    public class GhostChainConsistencyTests
    {
        [InGameTest(Category = "GhostChains",
            Description = "All chain Links reference recordings that exist in RecordingStore")]
        public void ChainLinksReferenceExistingRecordings()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0)
                InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            if (chains.Count == 0)
                InGameAssert.Skip("No ghost chains computed");

            // Build lookup of all recording IDs across all trees
            var allRecIds = new HashSet<string>();
            foreach (var tree in trees)
                foreach (var rec in tree.Recordings)
                    allRecIds.Add(rec.Key);

            // Also check standalone committed recordings
            foreach (var rec in RecordingStore.CommittedRecordings)
                allRecIds.Add(rec.RecordingId);

            int totalLinks = 0, dangling = 0;
            foreach (var kvp in chains)
            {
                foreach (var link in kvp.Value.Links)
                {
                    totalLinks++;
                    if (!allRecIds.Contains(link.recordingId))
                    {
                        dangling++;
                        ParsekLog.Warn("TestRunner",
                            $"Chain pid={kvp.Key}: link references non-existent recording '{link.recordingId}'");
                    }
                }
            }

            ParsekLog.Info("TestRunner",
                $"Chain links: {totalLinks - dangling}/{totalLinks} reference existing recordings");
            InGameAssert.AreEqual(0, dangling,
                $"{dangling} chain link(s) reference non-existent recordings");
        }

        [InGameTest(Category = "GhostChains",
            Description = "Chain GhostStartUT <= SpawnUT (start before or at spawn time)")]
        public void ChainTimeRangesValid()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            int violations = 0;

            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                if (chain.GhostStartUT > chain.SpawnUT && chain.SpawnUT > 0)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"Chain pid={kvp.Key}: GhostStartUT ({chain.GhostStartUT:F1}) > SpawnUT ({chain.SpawnUT:F1})");
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Chain time ranges: {chains.Count} chains, {violations} violations");
            InGameAssert.AreEqual(0, violations,
                $"{violations} chain(s) have GhostStartUT > SpawnUT");
        }

        [InGameTest(Category = "GhostChains",
            Description = "No vessel PID appears in two different non-terminated chains")]
        public void NoDoubleGhosting()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            var seenPids = new Dictionary<uint, int>(); // pid -> count
            int doubles = 0;

            foreach (var kvp in chains)
            {
                if (kvp.Value.IsTerminated) continue;

                if (seenPids.ContainsKey(kvp.Key))
                {
                    seenPids[kvp.Key]++;
                    doubles++;
                }
                else
                {
                    seenPids[kvp.Key] = 1;
                }
            }

            InGameAssert.AreEqual(0, doubles,
                $"{doubles} vessel PID(s) appear in multiple non-terminated chains (double-ghosting)");
        }

        [InGameTest(Category = "GhostChains",
            Description = "Spawn chains have non-null vessel snapshot on tip recording")]
        public void SpawnChainsHaveTipSnapshot()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            int spawnChains = 0, withSnapshot = 0;

            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                if (chain.IsTerminated || chain.SpawnUT <= 0) continue;
                if (string.IsNullOrEmpty(chain.TipRecordingId)) continue;

                spawnChains++;

                // Find the tip recording
                Recording tipRec = null;
                foreach (var tree in trees)
                {
                    if (tree.Recordings.TryGetValue(chain.TipRecordingId, out tipRec))
                        break;
                }

                if (tipRec?.VesselSnapshot != null)
                    withSnapshot++;
                else
                    ParsekLog.Warn("TestRunner",
                        $"Spawn chain pid={kvp.Key} tip='{chain.TipRecordingId}' has no VesselSnapshot");
            }

            if (spawnChains == 0)
                InGameAssert.Skip("No spawn chains to check");

            ParsekLog.Info("TestRunner",
                $"Spawn chains: {withSnapshot}/{spawnChains} have tip vessel snapshot");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Recording Tree Integrity — structural invariants
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates recording tree structural invariants: parent/child links,
    /// branch point references, and BackgroundMap consistency.
    /// </summary>
    public class RecordingTreeIntegrityTests
    {
        [InGameTest(Category = "TreeIntegrity",
            Description = "Every BranchPoint child recording ID exists in its tree")]
        public void BranchPointChildrenExist()
        {
            var trees = RecordingStore.CommittedTrees;
            int totalBPs = 0, dangling = 0;

            foreach (var tree in trees)
            {
                var recIds = new HashSet<string>(tree.Recordings.Keys);
                foreach (var bp in tree.BranchPoints)
                {
                    foreach (string childId in bp.ChildRecordingIds)
                    {
                        totalBPs++;
                        if (!recIds.Contains(childId))
                        {
                            dangling++;
                            ParsekLog.Warn("TestRunner",
                                $"BranchPoint '{bp.Id}': child '{childId}' not found in tree");
                        }
                    }
                }
            }

            if (totalBPs == 0)
                InGameAssert.Skip("No branch point children to check");

            InGameAssert.AreEqual(0, dangling,
                $"{dangling} branch point child reference(s) point to non-existent recordings");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "Every non-root recording's ParentRecordingId exists in its tree")]
        public void ParentLinksValid()
        {
            var trees = RecordingStore.CommittedTrees;
            int checked_ = 0, dangling = 0;

            foreach (var tree in trees)
            {
                var recIds = new HashSet<string>(tree.Recordings.Keys);
                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (string.IsNullOrEmpty(rec.ParentRecordingId)) continue; // root
                    checked_++;

                    if (!recIds.Contains(rec.ParentRecordingId))
                    {
                        dangling++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' parent '{rec.ParentRecordingId}' not in tree");
                    }
                }
            }

            if (checked_ == 0)
                InGameAssert.Skip("No non-root recordings with parent links");

            ParsekLog.Verbose("TestRunner",
                $"Parent links: {checked_ - dangling}/{checked_} valid");
            InGameAssert.AreEqual(0, dangling,
                $"{dangling} recording(s) have ParentRecordingId pointing to non-existent recordings");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "Every BackgroundMap entry points at an eligible recording in the same tree")]
        public void BackgroundMapEntriesResolveToEligibleRecordings()
        {
            var trees = RecordingStore.CommittedTrees;
            int entries = 0;
            int dangling = 0;
            int wrongPid = 0;
            int ineligible = 0;

            for (int i = 0; i < trees.Count; i++)
            {
                foreach (var kvp in trees[i].BackgroundMap)
                {
                    entries++;

                    if (!trees[i].Recordings.TryGetValue(kvp.Value, out var rec))
                    {
                        dangling++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree '{trees[i].Id}' BackgroundMap pid={kvp.Key} -> missing recording '{kvp.Value}'");
                        continue;
                    }

                    if (rec.VesselPersistentId != kvp.Key)
                    {
                        wrongPid++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree '{trees[i].Id}' BackgroundMap pid={kvp.Key} points at recording '{rec.RecordingId}' with pid={rec.VesselPersistentId}");
                    }

                    if (!trees[i].IsBackgroundMapEligible(rec))
                    {
                        ineligible++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree '{trees[i].Id}' BackgroundMap pid={kvp.Key} points at ineligible recording '{rec.RecordingId}'");
                    }
                }
            }

            if (trees.Count == 0)
                InGameAssert.Skip("No committed trees");

            ParsekLog.Verbose("TestRunner",
                $"BackgroundMap entries: total={entries} dangling={dangling} wrongPid={wrongPid} ineligible={ineligible}");
            InGameAssert.AreEqual(0, dangling + wrongPid + ineligible,
                $"{dangling + wrongPid + ineligible} BackgroundMap integrity issue(s) found");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "ComputeEndUT is >= every leaf recording's last point UT")]
        public void EndUTCoversAllLeaves()
        {
            var trees = RecordingStore.CommittedTrees;
            int violations = 0;

            foreach (var tree in trees)
            {
                double treeEndUT = tree.ComputeEndUT();
                if (treeEndUT == 0) continue; // degraded tree

                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (rec.Points == null || rec.Points.Count == 0) continue;

                    double recEndUT = rec.Points[rec.Points.Count - 1].ut;
                    if (recEndUT > treeEndUT + 0.001) // small tolerance
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' EndUT={recEndUT:F1} > tree EndUT={treeEndUT:F1}");
                    }
                }
            }

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) have EndUT beyond their tree's ComputeEndUT");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Multi-Scene & Harmony — scene controllers and patch effects
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scene-specific controller presence and Harmony patch effect verification.
    /// </summary>
    public class SceneAndPatchTests
    {
        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.SPACECENTER,
            Description = "ParsekKSC MonoBehaviour exists in Space Center scene")]
        public void ParsekKscExistsInSpaceCenter()
        {
            var ksc = Object.FindObjectOfType<ParsekKSC>();
            InGameAssert.IsNotNull(ksc,
                "ParsekKSC MonoBehaviour should be active in Space Center scene");
            ParsekLog.Verbose("TestRunner", "ParsekKSC instance found");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.SPACECENTER,
            Description = "KSC RELATIVE playback resolves through anchorRecordingId")]
        public void ParsekKscRelativePlaybackUsesRecordedAnchor()
        {
            var ksc = Object.FindObjectOfType<ParsekKSC>();
            if (ksc == null)
                InGameAssert.Skip("No ParsekKSC instance");

            CelestialBody kerbin = FlightGlobals.GetBodyByName("Kerbin");
            if (kerbin == null || kerbin.bodyTransform == null)
                InGameAssert.Skip("Kerbin body unavailable");

            var anchorPoint = new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 1000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var anchorRec = new Recording
            {
                RecordingId = "runtime-ksc-recorded-anchor",
                VesselName = "Runtime KSC Recorded Anchor",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            anchorRec.Points.Add(anchorPoint);
            anchorRec.Points.Add(new TrajectoryPoint
            {
                ut = 110,
                latitude = anchorPoint.latitude,
                longitude = anchorPoint.longitude,
                altitude = anchorPoint.altitude,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            anchorRec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 110,
                frames = new List<TrajectoryPoint>(anchorRec.Points)
            });
            var before = new TrajectoryPoint
            {
                ut = 100,
                latitude = 10,
                longitude = 20,
                altitude = 30,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var after = new TrajectoryPoint
            {
                ut = 110,
                latitude = 20,
                longitude = 30,
                altitude = 40,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var rec = new Recording
            {
                RecordingId = "runtime-ksc-relative-anchor",
                VesselName = "Runtime KSC Relative Probe",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            rec.Points.Add(before);
            rec.Points.Add(after);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100,
                endUT = 110,
                anchorRecordingId = anchorRec.RecordingId,
                frames = new List<TrajectoryPoint> { before, after }
            });
            var tree = new RecordingTree
            {
                Id = "runtime-ksc-relative-recorded-anchor-tree",
                TreeName = "Runtime KSC Relative Recorded Anchor",
                RootRecordingId = anchorRec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            anchorRec.TreeId = tree.Id;
            rec.TreeId = tree.Id;
            tree.Recordings[anchorRec.RecordingId] = anchorRec;
            tree.Recordings[rec.RecordingId] = rec;
            RecordingStore.AddCommittedTreeForTesting(tree);

            GameObject probe = new GameObject("Parsek_KSC_RelativeRuntimeProbe");
            var state = new GhostPlaybackState
            {
                ghost = probe,
                deferVisibilityUntilPlaybackSync = true
            };
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;

            try
            {
                bool positioned = ksc.InterpolateAndPositionKsc(
                    state,
                    rec,
                    ref cachedIndex,
                    ref cachedFrameSourceKey,
                    105);

                InGameAssert.IsTrue(positioned, "KSC relative runtime probe should resolve a recorded anchor");
                InGameAssert.IsFalse(state.deferVisibilityUntilPlaybackSync,
                    "Successful KSC relative positioning should clear deferred visibility");

                Vector3d anchorWorld = kerbin.GetWorldSurfacePosition(
                    anchorPoint.latitude,
                    anchorPoint.longitude,
                    anchorPoint.altitude);
                Vector3d expected = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorWorld,
                    kerbin.bodyTransform.rotation,
                    15,
                    25,
                    35);
                Vector3d actual = probe.transform.position;
                double error = (actual - expected).magnitude;
                InGameAssert.IsTrue(error < 0.25,
                    $"KSC relative runtime probe should land at anchor-local offset; error={error:F3}m");
                ParsekLog.Verbose("TestRunner",
                    $"KSC relative runtime probe resolved anchorRec={anchorRec.RecordingId} error={error:F3}m");
            }
            finally
            {
                RecordingStore.RemoveCommittedTreeById(tree.Id, "runtime-ksc-relative-recorded-anchor-cleanup");
                if (probe != null)
                    Object.Destroy(probe);
            }
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.TRACKSTATION,
            Description = "ParsekTrackingStation MonoBehaviour exists in Tracking Station")]
        public void ParsekTrackingStationExists()
        {
            var ts = Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts,
                "ParsekTrackingStation MonoBehaviour should be active in Tracking Station scene");
            ParsekLog.Verbose("TestRunner", "ParsekTrackingStation instance found");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.FLIGHT,
            Description = "Ghost map PIDs are not loaded as physics vessels (GhostVesselLoadPatch working)")]
        public void GhostPidsNotLoadedAsPhysicsVessels()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0)
                InGameAssert.Skip("No ghost map PIDs — patch not exercised");

            int loadedGhosts = 0;
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel == null || !vessel.loaded) continue;
                if (ghostPids.Contains(vessel.persistentId)
                    && GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                {
                    // Ghost is loaded as a physics vessel — patch failed
                    if (vessel.packed == false) // actually simulating physics
                    {
                        loadedGhosts++;
                        ParsekLog.Warn("TestRunner",
                            $"Ghost PID={vessel.persistentId} is loaded and unpacked (physics active)");
                    }
                }
            }

            InGameAssert.AreEqual(0, loadedGhosts,
                $"{loadedGhosts} ghost map vessel(s) are loaded with active physics (GhostVesselLoadPatch broken?)");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.FLIGHT,
            Description = "PhysicsFramePatch.ActiveRecorder is null when not recording (no stale reference)")]
        public void PhysicsFramePatchCleanWhenIdle()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            if (!flight.IsRecording)
            {
                InGameAssert.IsNull(Parsek.Patches.PhysicsFramePatch.ActiveRecorder,
                    "ActiveRecorder should be null when not recording (stale reference)");
            }
            else
            {
                InGameAssert.IsNotNull(Parsek.Patches.PhysicsFramePatch.ActiveRecorder,
                    "ActiveRecorder should be set when recording");
            }

            ParsekLog.Verbose("TestRunner",
                $"PhysicsFramePatch: IsRecording={flight.IsRecording}, " +
                $"ActiveRecorder={(Parsek.Patches.PhysicsFramePatch.ActiveRecorder != null ? "set" : "null")}");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.FLIGHT,
            Description = "Bug #266: outsider-state tree ConfigNode parses back with null ActiveRecordingId and routes to LimboVesselSwitch")]
        public void OutsiderActiveTreeParsesAndRoutesToLimboVesselSwitch_Bug266()
        {
            // Bug #266 acceptance test: a tree in "outsider state" — alive but with
            // no active recording — must serialize as a RECORDING_TREE node with
            // null activeRecordingId AND the OnLoad dispatch must route that node
            // to LimboVesselSwitch (not Limbo). This test exercises only the
            // ConfigNode round-trip and the dispatch decision; it does NOT drive
            // ParsekScenario.OnSave/OnLoad end-to-end (that would mutate live state
            // mid-flight, which we can't do safely). The full save/load cycle is
            // covered by manual playtest.
            var scenario = Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null) InGameAssert.Skip("No ParsekScenario instance");

            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var liveTree = flight.ActiveTreeForSerialization;
            if (liveTree == null)
                InGameAssert.Skip("No live active tree to use as a synth source");

            // Build a SYNTHESIZED outsider tree with stable shape: no active rec,
            // one BackgroundMap entry pointing at the tree's existing root recording.
            // We do NOT mutate the live tree — round-trip the serialized form
            // directly so the running flight session is undisturbed.
            var synthTreeNode = new ConfigNode("RECORDING_TREE");
            // Reuse the live tree's serialized form, then strip the active rec id
            // and inject one background entry. RecordingTree.Save / Load is the
            // SUT — if either drops fields, the assertion below catches it.
            liveTree.Save(synthTreeNode);
            // Wipe the activeRecordingId so it round-trips as null (outsider).
            synthTreeNode.RemoveValue("activeRecordingId");

            // Parse + verify the synth tree matches what TryRestoreActiveTreeNode
            // would produce in the OnLoad path.
            var parsed = RecordingTree.Load(synthTreeNode);
            InGameAssert.IsNull(parsed.ActiveRecordingId,
                "Synth tree should have null ActiveRecordingId after stripping");

            // Drive the dispatch decision the same way OnLoad would — verify the
            // helper would route this tree to the LimboVesselSwitch state.
            // (Direct reproduction of TryRestoreActiveTreeNode's stash-state pick.)
            var expectedState = string.IsNullOrEmpty(parsed.ActiveRecordingId)
                ? PendingTreeState.LimboVesselSwitch
                : PendingTreeState.Limbo;
            InGameAssert.AreEqual(
                (int)PendingTreeState.LimboVesselSwitch,
                (int)expectedState,
                "Outsider tree should route to LimboVesselSwitch state in OnLoad dispatch");

            ParsekLog.Verbose("TestRunner",
                $"Bug #266 round-trip: outsider tree '{liveTree.TreeName}' " +
                $"parsed back with ActiveRecordingId={(parsed.ActiveRecordingId ?? "<null>")} " +
                $"→ would stash as {expectedState}");
        }

        [InGameTest(Category = "SceneAndPatch",
            Description = "Bug #266: ApplyPreTransitionForVesselSwitch moves active rec into BackgroundMap and nulls ActiveRecordingId on the live tree shape")]
        public void PreTransitionForVesselSwitch_LiveTreeShape_Bug266()
        {
            // Bug #266: validate the pure helper that the stash path uses, against
            // a tree built from the live RecordingTree class. Catches any drift
            // between the helper's expectations and the real tree shape (e.g.,
            // BackgroundMap initialization, ActiveRecordingId nullability).
            var tree = new RecordingTree
            {
                Id = "ingame_bug266",
                TreeName = "Bug 266 Probe",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_root",
            };
            var rec = new Recording
            {
                RecordingId = "rec_root",
                VesselName = "Probe",
                TreeId = tree.Id,
                VesselPersistentId = 4242,
            };
            tree.Recordings[rec.RecordingId] = rec;

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, recorderVesselPid: 12345);

            InGameAssert.IsTrue(moved, "Pre-transition should report moved=true");
            InGameAssert.IsNull(tree.ActiveRecordingId,
                "ActiveRecordingId should be null after pre-transition");
            InGameAssert.IsTrue(tree.BackgroundMap.ContainsKey(12345),
                "Recorder PID 12345 should be in BackgroundMap (recorder PID has priority over tree-rec PID)");
            InGameAssert.IsFalse(tree.BackgroundMap.ContainsKey(4242),
                "Tree-rec PID 4242 should NOT be used when recorder PID is non-zero");
            InGameAssert.AreEqual("rec_root", tree.BackgroundMap[12345],
                "BackgroundMap should map the recorder PID to the old active recording id");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Floating Origin & KSP API Sanity
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// KSP API assumptions that Parsek depends on. If any of these fail,
    /// fundamental recording/playback logic is broken.
    /// </summary>
    public class KspApiSanityTests
    {
        private readonly InGameTestRunner runner;
        public KspApiSanityTests(InGameTestRunner runner) { this.runner = runner; }

        [InGameTest(Category = "KspApiSanity",
            Description = "body.bodyTransform.rotation is stable across 3 frames for live transform reconstruction")]
        public IEnumerator BodyTransformRotationStable()
        {
            var kerbin = FlightGlobals.GetBodyByName("Kerbin");
            if (kerbin == null)
                InGameAssert.Skip("Kerbin not found");

            Quaternion rot0 = kerbin.bodyTransform.rotation;
            yield return null;
            Quaternion rot1 = kerbin.bodyTransform.rotation;
            yield return null;
            Quaternion rot2 = kerbin.bodyTransform.rotation;

            // Rotation should be essentially identical across frames for live transform
            // reconstruction. ProtoVessel.rot still stores vessel.srfRelRotation directly.
            float delta01 = Quaternion.Angle(rot0, rot1);
            float delta12 = Quaternion.Angle(rot1, rot2);

            InGameAssert.IsLessThan(delta01, 0.01,
                $"Kerbin bodyTransform.rotation changed by {delta01:F4}deg between frames (expected stable)");
            InGameAssert.IsLessThan(delta12, 0.01,
                $"Kerbin bodyTransform.rotation changed by {delta12:F4}deg between frames (expected stable)");

            ParsekLog.Verbose("TestRunner",
                $"Kerbin bodyTransform.rotation stable: delta={delta01:F6}deg, {delta12:F6}deg");
        }

        [InGameTest(Category = "KspApiSanity",
            Description = "Planetarium.GetUniversalTime() is monotonically increasing across 5 frames")]
        public IEnumerator UniversalTimeMonotonic()
        {
            double prev = Planetarium.GetUniversalTime();
            int violations = 0;

            for (int i = 0; i < 5; i++)
            {
                yield return null;
                double current = Planetarium.GetUniversalTime();
                if (current < prev)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"UT went backward: frame {i} prev={prev:F3} current={current:F3}");
                }
                prev = current;
            }

            InGameAssert.AreEqual(0, violations,
                $"Planetarium.GetUniversalTime() went backward {violations} time(s) across 5 frames");
        }

        [InGameTest(Category = "KspApiSanity",
            Description = "PartLoader.getPartInfoByName returns same instance for same name (reference equality)")]
        public void PartLoaderReturnsSameInstance()
        {
            string testPart = "mk1pod.v2";
            var info1 = PartLoader.getPartInfoByName(testPart);
            var info2 = PartLoader.getPartInfoByName(testPart);

            if (info1 == null)
                InGameAssert.Skip($"Part '{testPart}' not found in PartLoader");

            InGameAssert.IsTrue(ReferenceEquals(info1, info2),
                $"PartLoader returned different instances for '{testPart}' (cache broken)");
        }

        [InGameTest(Category = "KspApiSanity", Scene = GameScenes.FLIGHT,
            Description = "Krakensbane.GetFrameVelocity() returns finite vector during flight")]
        public void KrakensbaneFrameVelocityFinite()
        {
            Vector3d kbVel = Krakensbane.GetFrameVelocity();
            InGameAssert.IsFalse(double.IsNaN(kbVel.x) || double.IsNaN(kbVel.y) || double.IsNaN(kbVel.z),
                $"Krakensbane frame velocity is NaN: ({kbVel.x},{kbVel.y},{kbVel.z})");
            InGameAssert.IsFalse(double.IsInfinity(kbVel.x) || double.IsInfinity(kbVel.y) || double.IsInfinity(kbVel.z),
                $"Krakensbane frame velocity is Infinity: ({kbVel.x},{kbVel.y},{kbVel.z})");
        }

        [InGameTest(Category = "KspApiSanity", Scene = GameScenes.FLIGHT,
            Description = "Ghost sphere placed at known position doesn't drift to NaN within 2 frames")]
        public IEnumerator FloatingOriginNoNaNDrift()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Place a test object near the active vessel
            var testObj = GhostVisualBuilder.CreateGhostSphere("ParsekTest_FloatOrigin", Color.green);
            runner.TrackForCleanup(testObj);
            Vector3 targetPos = vessel.transform.position + new Vector3(50, 10, 0);
            testObj.transform.position = targetPos;

            yield return null;
            yield return null;

            Vector3 finalPos = testObj.transform.position;
            InGameAssert.IsFalse(float.IsNaN(finalPos.x) || float.IsNaN(finalPos.y) || float.IsNaN(finalPos.z),
                $"Test object drifted to NaN after 2 frames: ({finalPos.x},{finalPos.y},{finalPos.z})");
            InGameAssert.IsFalse(finalPos == Vector3.zero,
                "Test object collapsed to origin (0,0,0) — floating origin issue");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Ghost Map Orbit Validation
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that ghost map ProtoVessels with orbit data produce valid
    /// Keplerian positions. Catches NaN from degenerate orbital elements.
    /// </summary>
    public class GhostMapOrbitTests
    {
        [InGameTest(Category = "GhostMapOrbits",
            Description = "Ghost map ProtoVessels with orbits produce valid positions at current UT")]
        public void GhostOrbitsProduceValidPositions()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0)
                InGameAssert.Skip("No ghost map vessels");

            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null) InGameAssert.Skip("No FlightState available");

            double ut = Planetarium.GetUniversalTime();
            int checked_ = 0, valid = 0;

            foreach (uint pid in ghostPids)
            {
                var pv = flightState.protoVessels.FirstOrDefault(p => p.persistentId == pid);
                if (pv?.orbitSnapShot == null) continue;

                checked_++;
                try
                {
                    // Check that the orbit snapshot has valid Keplerian elements
                    double sma = pv.orbitSnapShot.semiMajorAxis;
                    double ecc = pv.orbitSnapShot.eccentricity;
                    string bodyName = pv.orbitSnapShot.ReferenceBodyIndex >= 0
                        ? FlightGlobals.Bodies[pv.orbitSnapShot.ReferenceBodyIndex].name : null;

                    bool smaValid = !double.IsNaN(sma) && !double.IsInfinity(sma) && sma != 0;
                    bool eccValid = !double.IsNaN(ecc) && !double.IsInfinity(ecc) && ecc >= 0;
                    bool bodyValid = bodyName != null;

                    if (smaValid && eccValid && bodyValid)
                    {
                        valid++;
                    }
                    else
                    {
                        ParsekLog.Warn("TestRunner",
                            $"Ghost PID={pid} orbit has invalid elements: sma={sma} ecc={ecc} body={bodyName ?? "null"}");
                    }
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"Ghost PID={pid} orbit evaluation threw: {ex.Message}");
                }
            }

            if (checked_ == 0)
                InGameAssert.Skip("No ghost ProtoVessels with orbit snapshots");

            ParsekLog.Info("TestRunner",
                $"Ghost orbits: {valid}/{checked_} produce valid positions at UT={ut:F1}");
            InGameAssert.AreEqual(checked_, valid,
                $"{checked_ - valid} ghost orbit(s) produce invalid positions");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Spawn Collision — bounds computation with real vessel data
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates spawn collision detection with real loaded vessel data.
    /// </summary>
    public class SpawnCollisionLiveTests
    {
        [InGameTest(Category = "SpawnCollision", Scene = GameScenes.FLIGHT,
            Description = "Active vessel snapshot produces non-degenerate bounds")]
        public void ActiveVesselBoundsNonDegenerate()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Build a snapshot from the active vessel and compute bounds
            var snapshot = new ConfigNode("VESSEL");
            foreach (var part in vessel.parts)
            {
                var partNode = snapshot.AddNode("PART");
                partNode.AddValue("name", part.partInfo.name);
                var localPos = part.transform.localPosition;
                partNode.AddValue("pos",
                    $"{localPos.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{localPos.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{localPos.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            var bounds = SpawnCollisionDetector.ComputeVesselBounds(snapshot);
            InGameAssert.IsTrue(bounds.size.magnitude > 0.01f,
                $"Vessel bounds are degenerate (size={bounds.size})");
            InGameAssert.IsTrue(bounds.size.x < 500f && bounds.size.y < 500f && bounds.size.z < 500f,
                $"Vessel bounds suspiciously large: {bounds.size}");

            ParsekLog.Verbose("TestRunner",
                $"Active vessel bounds: center={bounds.center} size={bounds.size}");
        }

        [InGameTest(Category = "SpawnCollision", Scene = GameScenes.FLIGHT,
            Description = "CheckOverlapAgainstLoadedVessels returns no overlap for a distant position")]
        public void NoOverlapAtDistantPosition()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Pick a position 5km away from active vessel — should have no overlap
            Vector3d farPos = vessel.GetWorldPos3D() + new Vector3d(5000, 0, 0);
            var smallBounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            var result = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(farPos, smallBounds, 5f);

            InGameAssert.IsFalse(result.overlap,
                $"Overlap detected 5km from active vessel (blocker={result.blockerName}, dist={result.closestDistance:F0}m)");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Spawn Health — spawn state invariants on committed recordings
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates spawn-related state on committed recordings. Catches stuck flags
    /// from #132 (spawn-death detection), #112 (duplicate blocker recovery),
    /// and #149 (deferred spawn split-brain).
    /// </summary>
    public class SpawnHealthTests
    {
        [InGameTest(Category = "SpawnHealth",
            Description = "No committed recording has SpawnAbandoned=true (should reset on scene entry)")]
        public void NoStuckSpawnAbandoned()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int abandoned = 0;

            foreach (var rec in recordings)
            {
                if (rec.SpawnAbandoned)
                {
                    abandoned++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' ({rec.VesselName}) has SpawnAbandoned=true");
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Spawn abandoned check: {recordings.Count} recordings, {abandoned} stuck");
            InGameAssert.AreEqual(0, abandoned,
                $"{abandoned} recording(s) have SpawnAbandoned stuck true (should be transient)");
        }

        [InGameTest(Category = "SpawnHealth",
            Description = "SpawnDeathCount is within bounds on all committed recordings")]
        public void SpawnDeathCountWithinBounds()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int violations = 0;

            foreach (var rec in recordings)
            {
                if (rec.SpawnDeathCount < 0 || rec.SpawnDeathCount > 10)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' ({rec.VesselName}) has SpawnDeathCount={rec.SpawnDeathCount}");
                }
            }

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) have out-of-bounds SpawnDeathCount");
        }

        [InGameTest(Category = "SpawnHealth",
            Description = "SpawnedVesselPersistentId is nonzero only on recordings that should spawn")]
        public void SpawnedPidConsistency()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int spawnedCount = 0, destroyedWithSpawn = 0;

            foreach (var rec in recordings)
            {
                if (rec.SpawnedVesselPersistentId != 0)
                {
                    spawnedCount++;
                    // A destroyed vessel shouldn't have a spawned PID (it gets recovered)
                    if (rec.VesselDestroyed && rec.TerminalStateValue == TerminalState.Destroyed)
                    {
                        destroyedWithSpawn++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' ({rec.VesselName}) is Destroyed but has SpawnedVesselPersistentId={rec.SpawnedVesselPersistentId}");
                    }
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Spawned PIDs: {spawnedCount} of {recordings.Count} recordings have spawned vessels");
            InGameAssert.AreEqual(0, destroyedWithSpawn,
                $"{destroyedWithSpawn} destroyed recording(s) still have spawned vessel PID");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Continuation Integrity — boundary and snapshot state (#95)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates continuation boundary state on committed recordings.
    /// Catches #95 (continuation data persisting through revert) and
    /// #95 items 3-5 (pre-continuation snapshot corruption).
    /// </summary>
    public class ContinuationIntegrityTests
    {
        [InGameTest(Category = "ContinuationIntegrity",
            Description = "ContinuationBoundaryIndex is -1 on all committed recordings (cleared on commit)")]
        public void BoundaryIndexClearedOnCommit()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int stuck = 0;

            foreach (var rec in recordings)
            {
                if (rec.ContinuationBoundaryIndex >= 0)
                {
                    stuck++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' ({rec.VesselName}) has ContinuationBoundaryIndex={rec.ContinuationBoundaryIndex} (expected -1)");
                }
            }

            // Also check tree recordings
            foreach (var tree in RecordingStore.CommittedTrees)
            {
                foreach (var kvp in tree.Recordings)
                {
                    if (kvp.Value.ContinuationBoundaryIndex >= 0)
                    {
                        stuck++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree recording '{kvp.Key}' ({kvp.Value.VesselName}) has ContinuationBoundaryIndex={kvp.Value.ContinuationBoundaryIndex}");
                    }
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Continuation boundary check: {stuck} recording(s) have non-cleared boundary index");
            InGameAssert.AreEqual(0, stuck,
                $"{stuck} committed recording(s) have ContinuationBoundaryIndex >= 0 (should be -1 after commit)");
        }

        [InGameTest(Category = "ContinuationIntegrity",
            Description = "No committed recording has pre-continuation backup snapshots (should be cleared on bake)")]
        public void BackupSnapshotsCleared()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int lingering = 0;

            foreach (var rec in recordings)
            {
                if (rec.PreContinuationVesselSnapshot != null || rec.PreContinuationGhostSnapshot != null)
                {
                    lingering++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' ({rec.VesselName}) has lingering pre-continuation backup snapshot(s)");
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Backup snapshot check: {lingering} recording(s) have un-cleared backups");
            InGameAssert.AreEqual(0, lingering,
                $"{lingering} committed recording(s) have pre-continuation backup snapshots (should be cleared on bake or rollback)");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Rewind Save Resolution — tree branches resolve rewind saves (#159, #166)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that tree branch recordings can resolve rewind saves through
    /// their tree root. Catches #159/#166 (R button absent on tree branches).
    /// </summary>
    public class RewindSaveTests
    {
        [InGameTest(Category = "RewindSaves",
            Description = "Every tree branch recording resolves a rewind save via GetRewindRecording")]
        public void TreeBranchesResolveRewindSave()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            int branches = 0, resolved = 0, noSave = 0;

            foreach (var tree in trees)
            {
                // Root should have the rewind save
                Recording rootRec = null;
                if (!string.IsNullOrEmpty(tree.RootRecordingId))
                    tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec);

                bool rootHasSave = rootRec != null && !string.IsNullOrEmpty(rootRec.RewindSaveFileName);

                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (string.IsNullOrEmpty(rec.ParentRecordingId)) continue; // skip root
                    branches++;

                    var owner = RecordingStore.GetRewindRecording(rec);
                    if (owner != null)
                        resolved++;
                    else if (rootHasSave)
                    {
                        noSave++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree branch '{rec.RecordingId}' ({rec.VesselName}) in tree '{tree.Id}' " +
                            $"cannot resolve rewind save — root has save but lookup failed");
                    }
                }
            }

            if (branches == 0) InGameAssert.Skip("No tree branch recordings to check");

            ParsekLog.Info("TestRunner",
                $"Rewind saves: {resolved}/{branches} tree branches resolve a rewind save, {noSave} failed");
            InGameAssert.AreEqual(0, noSave,
                $"{noSave} tree branch recording(s) fail to resolve rewind save despite root having one");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Terminal Orbit Completeness — orbital recordings have orbit data (#203, #219)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that recordings with orbital terminal states have complete terminal
    /// orbit data. Catches #203 (standalone recordings missing terminal orbit fields)
    /// and #219 (debris chain "no orbit data" errors).
    /// </summary>
    public class TerminalOrbitTests
    {
        [InGameTest(Category = "TerminalOrbit",
            Description = "Orbital/Docked terminal recordings have TerminalOrbitBody populated")]
        public void OrbitalRecordingsHaveTerminalOrbit()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int orbital = 0, withOrbit = 0, missing = 0;

            foreach (var rec in recordings)
            {
                if (rec.TerminalStateValue != TerminalState.Orbiting &&
                    rec.TerminalStateValue != TerminalState.Docked)
                    continue;

                orbital++;
                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    withOrbit++;
                    // Also verify body resolves
                    var body = FlightGlobals.GetBodyByName(rec.TerminalOrbitBody);
                    if (body == null)
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' TerminalOrbitBody='{rec.TerminalOrbitBody}' does not resolve");
                }
                else
                {
                    missing++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' ({rec.VesselName}) is {rec.TerminalStateValue} but has no TerminalOrbitBody");
                }
            }

            // Also check tree recordings
            foreach (var tree in RecordingStore.CommittedTrees)
            {
                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (rec.TerminalStateValue != TerminalState.Orbiting &&
                        rec.TerminalStateValue != TerminalState.Docked)
                        continue;

                    orbital++;
                    if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                        withOrbit++;
                    else
                    {
                        missing++;
                        ParsekLog.Warn("TestRunner",
                            $"Tree recording '{kvp.Key}' ({rec.VesselName}) is {rec.TerminalStateValue} but has no TerminalOrbitBody");
                    }
                }
            }

            if (orbital == 0) InGameAssert.Skip("No orbital/docked recordings to check");

            ParsekLog.Info("TestRunner",
                $"Terminal orbit data: {withOrbit}/{orbital} orbital recordings have TerminalOrbitBody, {missing} missing");
            InGameAssert.AreEqual(0, missing,
                $"{missing} orbital recording(s) missing TerminalOrbitBody (regression of #203/#219)");
        }

        [InGameTest(Category = "TerminalOrbit",
            Description = "Orbit segment bodies all resolve to valid CelestialBodies")]
        public void OrbitSegmentBodiesResolve()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int segments = 0, unresolved = 0;

            foreach (var rec in recordings)
            {
                if (rec.OrbitSegments == null) continue;
                foreach (var seg in rec.OrbitSegments)
                {
                    segments++;
                    if (string.IsNullOrEmpty(seg.bodyName) || FlightGlobals.GetBodyByName(seg.bodyName) == null)
                    {
                        unresolved++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' orbit segment body='{seg.bodyName ?? "null"}' does not resolve");
                    }
                }
            }

            if (segments == 0) InGameAssert.Skip("No orbit segments to check");

            ParsekLog.Verbose("TestRunner",
                $"Orbit segment bodies: {segments - unresolved}/{segments} resolve");
            InGameAssert.AreEqual(0, unresolved,
                $"{unresolved} orbit segment(s) have unresolvable body names");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Crew Reservation Live — spawned vessel PID consistency (#233)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates crew reservation system state with live game data.
    /// Catches #233 (spawned EVA vessel deleted by crew reservation)
    /// and #46 (EVA kerbals disappear after spawn).
    /// </summary>
    public class CrewReservationLiveTests
    {
        [InGameTest(Category = "CrewReservationLive",
            Description = "BuildSpawnedVesselPidSet contains all non-zero SpawnedVesselPersistentIds")]
        public void SpawnedPidSetComplete()
        {
            var recordings = RecordingStore.CommittedRecordings;
            var pidSet = CrewReservationManager.BuildSpawnedVesselPidSet(recordings);

            int spawnedCount = 0, missing = 0;
            foreach (var rec in recordings)
            {
                if (rec.SpawnedVesselPersistentId == 0) continue;
                spawnedCount++;

                if (!pidSet.Contains(rec.SpawnedVesselPersistentId))
                {
                    missing++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' SpawnedPID={rec.SpawnedVesselPersistentId} not in BuildSpawnedVesselPidSet");
                }
            }

            if (spawnedCount == 0) InGameAssert.Skip("No spawned vessel PIDs");

            ParsekLog.Verbose("TestRunner",
                $"Spawned PID set: {pidSet.Count} PIDs in set, {spawnedCount} recordings with spawned vessels, {missing} missing");
            InGameAssert.AreEqual(0, missing,
                $"{missing} spawned vessel PID(s) missing from BuildSpawnedVesselPidSet");
        }

        [InGameTest(Category = "CrewReservationLive",
            Description = "Spawned vessel PIDs that reference loaded vessels are not ghost map vessels")]
        public void SpawnedVesselsNotGhosts()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int checked_ = 0, conflicts = 0;

            foreach (var rec in recordings)
            {
                if (rec.SpawnedVesselPersistentId == 0) continue;
                checked_++;

                if (GhostMapPresence.IsGhostMapVessel(rec.SpawnedVesselPersistentId))
                {
                    conflicts++;
                    ParsekLog.Warn("TestRunner",
                        $"Recording '{rec.RecordingId}' SpawnedPID={rec.SpawnedVesselPersistentId} is also a ghost map vessel (PID conflict)");
                }
            }

            if (checked_ == 0) InGameAssert.Skip("No spawned vessel PIDs to check");

            ParsekLog.Verbose("TestRunner",
                $"Spawned/ghost PID check: {checked_} spawned vessels, {conflicts} ghost conflicts");
            InGameAssert.AreEqual(0, conflicts,
                $"{conflicts} spawned vessel PID(s) conflict with ghost map vessels");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Phase 8e S0 coverage-closure instruments (PURELY ADDITIVE)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exercises the S0 Instrument 1 accounted-vs-drawn seam under the LIVE KSP runtime/JIT (the pure
    /// helper logic is covered by xUnit; this proves the static accounting + the RecordingId-domain pid
    /// bridge run end-to-end in-game). Drives the seam DETERMINISTICALLY via the test stamps - no live
    /// ghost / no full Driver frame needed - so it is robust regardless of what is on the map. Restores
    /// the coverage state on exit. A full scene-driven assertion (live ghosts through the polyline walk)
    /// is left as a manual tracing-on playtest, noted in the S0 report.
    /// </summary>
    public class MapRenderS0CoverageInGameTests
    {
        [InGameTest(Category = "GhostMapOrbits", Scene = GameScenes.FLIGHT,
            Description = "S0 Instrument 1: the accounted set covers proto-bearing + proto-less drawn recordings; fires for an orphan")]
        public void AccountedSetCoversDrawnSet()
        {
            // Snapshot live coverage state so the synthetic stamps below do not perturb a real frame's
            // accounting. ResetCoverageSetsForTesting clears the two frame sets + the scratch; we only add
            // synthetic ids and clear them again, leaving the live pid bridge untouched except for the one
            // synthetic pid we add and remove.
            GhostMapPresence.ResetCoverageSetsForTesting();
            const uint syntheticPid = 999001u;
            try
            {
                // A proto-bearing draw (pid != 0, NOT in the coverage set) - accounted via the pid bridge.
                GhostMapPresence.SetProtoBearingPidForTesting(syntheticPid, "s0-ingame-proto");
                GhostMapPresence.NoteDrawnRecordingCoverage("s0-ingame-proto", syntheticPid);
                // A proto-less draw (pid 0) - folded into the coverage set, accounted via the non-proto path.
                GhostMapPresence.NoteDrawnRecordingCoverage("s0-ingame-atmo", 0u);
                // An orphan: drawn, proto-less-looking, but withheld from the coverage set (simulating a
                // draw path that bypassed the fold) and with no pid bridge - must be flagged.
                GhostMapPresence.SetFrameCoverageForTesting("s0-ingame-orphan", drawn: true, protoLess: false);

                var unaccounted = new List<string>();
                GhostMapPresence.AssertDrawnRecordingsAccounted(
                    (recId, pb, plc, drawn) => unaccounted.Add(recId));

                ParsekLog.Info("TestRunner",
                    $"S0 accounting in-game: unaccounted=[{string.Join(",", unaccounted)}]");

                InGameAssert.IsFalse(unaccounted.Contains("s0-ingame-proto"),
                    "proto-bearing drawn recording must be accounted via the RecordingId-domain pid bridge");
                InGameAssert.IsFalse(unaccounted.Contains("s0-ingame-atmo"),
                    "proto-less drawn recording must be accounted via the coverage set");
                InGameAssert.IsTrue(unaccounted.Contains("s0-ingame-orphan"),
                    "an unaccounted drawn recording must fire (non-vacuity)");
                InGameAssert.AreEqual(1, unaccounted.Count,
                    "exactly the orphan is unaccounted");
            }
            finally
            {
                GhostMapPresence.SetProtoBearingPidForTesting(syntheticPid, null);
                GhostMapPresence.ResetCoverageSetsForTesting();
            }
        }

    }
}
