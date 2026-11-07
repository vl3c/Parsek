using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Split event detection and tree branching: split / merge branch creation, deferred split
    // checks, background map entries, breakup children and the fallback commit of a split
    // recorder.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Checks whether a vessel type alone qualifies as trackable.
        /// SpaceObject (asteroids, comets) and EVA kerbals are always trackable
        /// purely from the type. Other types require a module check
        /// (see <see cref="IsTrackableVessel"/>).
        /// </summary>
        internal static bool IsTrackableVesselType(VesselType vesselType)
        {
            return vesselType == VesselType.SpaceObject
                || vesselType == VesselType.EVA;
        }

        /// <summary>
        /// Determines whether a vessel is trackable (should get its own tree branch).
        /// Trackable = SpaceObject, EVA kerbal, OR has ModuleCommand
        /// (covers crewed pods and probe cores). Debris, spent boosters, fairings,
        /// etc. are NOT trackable.
        ///
        /// <para>
        /// EVA kerbals carry <c>KerbalEVA</c> rather than <c>ModuleCommand</c>, but
        /// they are directly controllable by the player — so for split-event
        /// classification they must count as a controllable output. Without this,
        /// an EVA split (mother vessel + kerbal) classifies as single-controllable,
        /// no <see cref="RewindPoint"/> is authored, and a destroyed EVA kerbal
        /// recording cannot satisfy <see cref="EffectiveState.IsUnfinishedFlight"/>
        /// (no RP back-reference for it to match against).
        /// </para>
        /// </summary>
        internal static bool IsTrackableVessel(Vessel v)
        {
            if (v == null) return false;

            // Space objects (asteroids, comets) are always trackable
            if (v.vesselType == VesselType.SpaceObject) return true;

            // EVA kerbals are controllable by the player even though their part
            // carries KerbalEVA rather than ModuleCommand.
            if (v.isEVA || v.vesselType == VesselType.EVA) return true;

            // Check for command capability (ModuleCommand covers both crewed pods and probe cores)
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.FindModuleImplementing<ModuleCommand>() != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Does a vessel that separated from a recorded vessel get its own recorded branch (a
        /// child recording with no debris lifetime that spawns at its end), rather than being
        /// treated as debris? Every trackable vessel does (<see cref="IsTrackableVessel"/>), and so
        /// does any vessel with a kerbal aboard: a crew cabin with no command module (a
        /// Hitchhiker, a Mk1 crew cabin) cannot be flown, but its kerbal is alive in a real
        /// vessel and must not read as lost (SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS).
        /// Recording only: Rewind Points and Re-Fly slots still need a controllable vessel and
        /// keep reading <see cref="IsTrackableVessel"/>.
        /// </summary>
        internal static bool ShouldRecordAsOwnBranch(Vessel v)
        {
            if (v == null) return false;
            bool trackable = IsTrackableVessel(v);
            int crew = trackable ? 0 : SafeCrewCount(v);
            bool own = ShouldRecordAsOwnBranch(trackable, crew);
            if (own && !trackable)
            {
                ParsekLog.Info("Flight",
                    $"Separated vessel pid={v.persistentId} name='{v.vesselName ?? "<unnamed>"}' has no " +
                    $"command module but carries crew={crew.ToString(CultureInfo.InvariantCulture)}: " +
                    "recorded as its own branch, not debris (no Re-Fly slot)");
            }
            return own;
        }

        /// <summary>Pure half of <see cref="ShouldRecordAsOwnBranch(Vessel)"/>.</summary>
        internal static bool ShouldRecordAsOwnBranch(bool trackable, int crewCount)
        {
            return trackable || crewCount > 0;
        }

        private static int SafeCrewCount(Vessel v)
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

        internal static bool ShouldIncludeVesselInDeferredBreakupScan(Vessel v, out string rejectReason)
        {
            rejectReason = null;
            if (v == null)
            {
                rejectReason = "null-vessel";
                return false;
            }

            if (IsSpaceObjectLikeBreakupScanReject(v.vesselType, EnumeratePartNames(v), EnumerateModuleNames(v),
                out rejectReason))
                return false;

            return true;
        }

        internal static bool IsSpaceObjectLikeBreakupScanReject(
            VesselType vesselType,
            IEnumerable<string> partNames,
            IEnumerable<string> moduleNames,
            out string rejectReason)
        {
            rejectReason = null;

            if (vesselType == VesselType.SpaceObject)
            {
                rejectReason = "space-object-type";
                return true;
            }

            if (ContainsAsteroidOrCometPart(partNames))
            {
                rejectReason = "asteroid-comet-part";
                return true;
            }

            if (ContainsAsteroidOrCometModule(moduleNames))
            {
                rejectReason = "asteroid-comet-module";
                return true;
            }

            return false;
        }

        private static IEnumerable<string> EnumeratePartNames(Vessel v)
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

        private static IEnumerable<string> EnumerateModuleNames(Vessel v)
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

        private static bool ContainsAsteroidOrCometPart(IEnumerable<string> partNames)
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

        private static bool ContainsAsteroidOrCometModule(IEnumerable<string> moduleNames)
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

        /// <summary>
        /// Determines whether a debris vessel is significant enough to record.
        /// Filters out trivial crash fragments (single struts, panels, shroud pieces)
        /// while keeping meaningful debris like spent boosters and stages.
        /// </summary>
        internal static bool ShouldRecordDebris(Vessel v)
        {
            if (v == null) return false;
            int partCount = v.parts?.Count ?? 0;
            float mass = (float)v.totalMass;
            return ShouldRecordDebris(partCount, mass);
        }

        /// <summary>
        /// Pure testable overload: debris is significant if it has at least
        /// 3 parts OR at least 0.5 tons of mass.
        /// </summary>
        internal static bool ShouldRecordDebris(int partCount, float mass)
        {
            const int MinPartCount = 3;
            const float MinMassTons = 0.5f;
            return partCount >= MinPartCount || mass >= MinMassTons;
        }

        /// <summary>
        /// Appends captured trajectory data (points, orbit segments, part events, flag events,
        /// segment events, track sections) from a source recording into the target, sorts
        /// part/flag/segment events by UT, and sets the target's ExplicitEndUT.
        /// If source is null, only ExplicitEndUT is set.
        /// </summary>
        internal static void AppendCapturedDataToRecording(Recording target, Recording source, double endUT)
        {
            if (source != null)
            {
                target.Points.AddRange(source.Points);
                target.OrbitSegments.AddRange(source.OrbitSegments);
                target.PartEvents.AddRange(source.PartEvents);
                target.FlagEvents.AddRange(source.FlagEvents);
                target.SegmentEvents.AddRange(source.SegmentEvents);
                target.TrackSections.AddRange(source.TrackSections);
                // STABLE sort: same-UT events preserve insertion order (#287).
                var sortedAppend = FlightRecorder.StableSortPartEventsByUT(target.PartEvents);
                target.PartEvents.Clear();
                target.PartEvents.AddRange(sortedAppend);
                var sortedFlagAppend = FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut);
                target.FlagEvents.Clear();
                target.FlagEvents.AddRange(sortedFlagAppend);
                var sortedSegAppend = FlightRecorder.StableSortByUT(target.SegmentEvents, e => e.ut);
                target.SegmentEvents.Clear();
                target.SegmentEvents.AddRange(sortedSegAppend);
                ApplyCapturedLogisticsMetadataToRecording(
                    target,
                    source,
                    "AppendCapturedDataToRecording");
                // Mark dirty so the next OnSave persists the appended data to
                // the .prec sidecar. Without this, data lives only in memory
                // and is lost on scene reload (see Recording.MarkFilesDirty
                // docstring and CHANGELOG 2026-04-09 bug #273).
                target.MarkFilesDirty();
            }
            target.ExplicitEndUT = endUT;
        }

        internal static bool ApplyCapturedLogisticsMetadataToRecording(
            Recording target,
            Recording source,
            string sourcePath)
        {
            if (target == null || source == null)
                return false;

            bool changed = false;

            if (target.StartResources == null && HasEntries(source.StartResources))
            {
                target.StartResources = RouteProofMetadata.CloneResourceManifest(source.StartResources);
                changed = true;
            }
            if (HasEntries(source.EndResources))
            {
                target.EndResources = RouteProofMetadata.CloneResourceManifest(source.EndResources);
                changed = true;
            }

            if (target.StartInventory == null && HasEntries(source.StartInventory))
            {
                target.StartInventory = RouteProofMetadata.CloneInventoryManifest(source.StartInventory);
                changed = true;
            }
            if (HasEntries(source.EndInventory))
            {
                target.EndInventory = RouteProofMetadata.CloneInventoryManifest(source.EndInventory);
                changed = true;
            }
            if (target.StartInventorySlots == 0 && source.StartInventorySlots != 0)
            {
                target.StartInventorySlots = source.StartInventorySlots;
                changed = true;
            }
            if (source.EndInventorySlots != 0)
            {
                target.EndInventorySlots = source.EndInventorySlots;
                changed = true;
            }

            if (target.StartCrew == null && HasEntries(source.StartCrew))
            {
                target.StartCrew = RouteProofMetadata.CloneCrewManifest(source.StartCrew);
                changed = true;
            }
            if (HasEntries(source.EndCrew))
            {
                target.EndCrew = RouteProofMetadata.CloneCrewManifest(source.EndCrew);
                changed = true;
            }

            if (source.DockTargetVesselPid != 0)
            {
                target.DockTargetVesselPid = source.DockTargetVesselPid;
                changed = true;
            }

            // M2 run manifest (plan D14): adopt-once semantics for the START
            // half, unconditional adopt for the END half. The START half is
            // write-once (captured at the recording's birth and usually already
            // written onto the tree recording by the recorder); the END half is
            // overwrite-per-active-stop - a chain-boundary stop abandoned by
            // ResumeAfterFalseAlarm leaves a stale END that the eventual real
            // stop's capture must replace.
            if (source.RouteRunManifest != null && !target.RunManifestVoided)
            {
                if (target.RouteRunManifest == null || !target.RouteRunManifest.HasStartHalf)
                {
                    target.RouteRunManifest = source.RouteRunManifest.DeepClone();
                }
                else
                {
                    target.RouteRunManifest.EndTransportResources =
                        RouteProofMetadata.CloneResourceManifest(
                            source.RouteRunManifest.EndTransportResources);
                    target.RouteRunManifest.EndCaptured = source.RouteRunManifest.EndCaptured;
                }
                changed = true;
            }
            else if (source.RouteRunManifest != null)
            {
                // Sticky void tombstone wins (M2 review follow-up): a voided
                // leg never re-adopts a manifest - the void is the fail-closed
                // verdict for the whole leg.
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata: run manifest NOT adopted (target voided) " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"}");
            }

            // Propagate the void tombstone itself (sticky, one-way).
            if (source.RunManifestVoided && !target.RunManifestVoided)
            {
                target.RunManifestVoided = true;
                changed = true;
            }

            // M2 harvest windows (plan D4/D14): the capture carries the leg's
            // FULL window list (recorder-side until stop forwarding), so adopt
            // it wholesale per active stop - same overwrite-per-active-stop
            // semantics as the run-manifest END half.
            if (source.RouteHarvestWindows != null && source.RouteHarvestWindows.Count > 0)
            {
                target.RouteHarvestWindows =
                    RouteProofMetadata.CloneHarvestWindows(source.RouteHarvestWindows);
                changed = true;
            }

            // START-DOCKED ORIGIN PROOF: WRITE-ONCE ADOPTION.
            // FlightRecorder.BuildCaptureRecording attaches the start-time proof to the
            // CAPTURE recording (RouteProofCapture.AttachEndManifestsAndForwardToCapture).
            // In always-tree mode that capture is never committed as-is, so before this the
            // proof died here on every path a flight has ever taken
            // (ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING, measured by H57's first
            // flight and corroborated by ROUTE_ORIGIN_PROOF=0 in H56's and H57's produced
            // saves).
            //
            // WHICH CALL WINS: THE FIRST ONE THAT CARRIES A PROOF. This helper is shared by
            // the ordinary tree-mode stop flush (FlushRecorderToTreeRecording, the path H57
            // red on), the undock split and the dock merge (both through
            // AppendCapturedDataToRecording). The origin is a BIRTH fact of the run, so a
            // later call must not restate it; and - the reason a bare assignment would be
            // wrong - FlightRecorder nulls pendingRouteOriginProof at EVERY recorder start,
            // so a split-then-stop sequence would otherwise blank a real proof back out.
            // Adopt only into a null slot; never clear, never overwrite.
            if (source.RouteOriginProof != null && target.RouteOriginProof == null)
            {
                target.RouteOriginProof = source.RouteOriginProof.DeepClone();
                changed = true;
                ParsekLog.Info("Flight",
                    $"Logistics metadata: RouteOriginProof adopted (write-once) " +
                    $"path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"originRoot={target.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"originName='{target.RouteOriginProof.StartDockedOriginVesselName ?? "<none>"}' " +
                    $"originType={target.RouteOriginProof.StartDockedOriginVesselType.ToString(CultureInfo.InvariantCulture)}");
            }
            else if (source.RouteOriginProof != null)
            {
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata: RouteOriginProof NOT adopted (target already carries one) " +
                    $"path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"targetOriginRoot={target.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"sourceOriginRoot={source.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)}");
            }

            // Route-window / transfer fields are NOT forwarded here: dock route windows are
            // written directly onto the merged child in CreateMergeBranch, and forwarding
            // them here too would double up that path.

            if (changed)
            {
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata copied: path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"startRes={(target.StartResources?.Count ?? 0)} endRes={(target.EndResources?.Count ?? 0)} " +
                    $"startInv={(target.StartInventory?.Count ?? 0)} endInv={(target.EndInventory?.Count ?? 0)} " +
                    $"dockTargetPid={target.DockTargetVesselPid} " +
                    $"runManifest={(target.RouteRunManifest != null ? (target.RouteRunManifest.IsComplete ? "complete" : "start-only") : "none")} " +
                    $"harvestWindows={(target.RouteHarvestWindows?.Count ?? 0)}");
            }

            return changed;
        }

        private static bool HasEntries<TKey, TValue>(Dictionary<TKey, TValue> dict)
        {
            return dict != null && dict.Count > 0;
        }

        /// <summary>
        /// When in tree mode, appends captured split data to the active tree recording
        /// instead of creating a standalone recording. Returns true if the data was
        /// successfully appended, false to fall through to the standalone path.
        /// Bug #297: FallbackCommitSplitRecorder was tree-mode-unaware and orphaned
        /// continuation data as ungrouped standalone recordings.
        /// </summary>
        internal static bool TryAppendCapturedToTree(RecordingTree tree, Recording captured)
        {
            if (tree == null || captured == null || captured.Points.Count < 2)
                return false;

            string targetId = tree.ActiveRecordingId ?? tree.RootRecordingId;
            if (targetId == null)
            {
                ParsekLog.Warn("Flight",
                    "TryAppendCapturedToTree: no active or root recording id — falling through to standalone");
                return false;
            }

            Recording targetRec;
            if (!tree.Recordings.TryGetValue(targetId, out targetRec))
            {
                ParsekLog.Warn("Flight",
                    $"TryAppendCapturedToTree: recording '{targetId}' not found in tree — falling through to standalone");
                return false;
            }

            double endUT = captured.Points[captured.Points.Count - 1].ut;
            AppendCapturedDataToRecording(targetRec, captured, endUT);

            if (captured.VesselDestroyed)
            {
                targetRec.VesselDestroyed = true;
                targetRec.StampTerminalState(TerminalState.Destroyed, "TryAppendCapturedToTree");
            }

            if (captured.MaxDistanceFromLaunch > targetRec.MaxDistanceFromLaunch)
                targetRec.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;

            targetRec.EndBiome = captured.EndBiome;

            // #416 R-button: bridge the captured rewind save / pre-launch budget / reserved
            // budget onto the tree root. FinalizeTreeRecordings' own CopyRewindSaveToRoot is
            // gated on this.recorder != null, but when a joint break on crash moves the
            // recorder into pendingSplitRecorder the main recorder is null by the time
            // FinalizeTreeRecordings runs — so without this call, crashed-vessel recordings
            // keep the on-disk parsek_rw_*.sfs but no recording ever references it and the
            // R button disappears. First-wins semantics inside CopyRewindSaveToRoot preserve
            // any legitimate pre-existing root data.
            CopyRewindSaveToRoot(tree, captured, logTag: "TryAppendCapturedToTree");

            ParsekLog.Info("Flight",
                $"TryAppendCapturedToTree: appended {captured.Points.Count} points to " +
                $"tree recording '{targetId}' (destroyed={captured.VesselDestroyed})");
            return true;
        }

        /// <summary>
        /// Applies a late destruction callback to a stopped recorder capture. Leaves
        /// <see cref="Recording.TerminalStateValue"/> as the pre-override diagnostic
        /// value; commit paths derive the final terminal state from VesselDestroyed.
        /// </summary>
        internal static bool ApplyPendingSplitDestructionToCapturedRecording(
            Recording captured,
            bool vesselDestroyedDuringRecording,
            string source)
        {
            if (captured == null || !vesselDestroyedDuringRecording)
                return false;
            if (captured.VesselDestroyed)
                return false;

            TerminalState? priorTerminal = captured.TerminalStateValue;
            captured.VesselDestroyed = true;

            ParsekLog.Info("Flight",
                $"Pending split destruction override: capture='{captured.RecordingId ?? "(null)"}' " +
                $"source={source ?? "(null)"} priorTerminal={priorTerminal?.ToString() ?? "null"} " +
                $"points={captured.Points?.Count ?? 0} snapshot={captured.VesselSnapshot != null}");
            return true;
        }

        /// <summary>
        /// Copies a stopped split capture onto the standalone fallback recording when no
        /// active tree accepts the capture.
        /// <para>The recorded vessel pid is stamped too. The capture itself does not carry
        /// it (<c>FlightRecorder.BuildCaptureRecording</c> leaves
        /// <c>VesselPersistentId</c> at 0), and every launch-identity guard keys on it: the
        /// rewind strip scope's same-vessel route, spawn adoption in FLIGHT / KSC /
        /// Tracking Station (<c>VesselSpawner.MaterializedSourceVesselExists(Recording)</c>)
        /// and the live-vessel ghost skip. A standalone recording without it reads as "no
        /// vessel", so a rewind keeps the live vessel it records and its terminal spawn
        /// builds a second copy of that vessel on top of it.</para>
        /// </summary>
        /// <param name="recordedVesselPid">The recorder's <c>RecordingVesselId</c>; 0 falls
        /// back to the capture snapshot's <c>persistentId</c>.</param>
        internal static void ApplyCapturedSplitStateToStandaloneRecording(
            Recording rec, Recording captured, uint recordedVesselPid = 0)
        {
            if (rec == null || captured == null)
                return;

            rec.VesselPersistentId = ResolveStandaloneRecordedVesselPid(
                recordedVesselPid, captured);
            rec.VesselSnapshot = captured.VesselSnapshot;
            rec.GhostVisualSnapshot = captured.GhostVisualSnapshot;
            // The capture already carries the launch guid (FlightRecorder stamps it in
            // BuildCaptureRecording); forwarding it here means the standalone fallback
            // recording does not have to wait for the load-time snapshot backfill to learn
            // which launch it recorded.
            rec.RecordedVesselGuid = captured.RecordedVesselGuid;
            rec.VesselDestroyed = captured.VesselDestroyed;
            rec.VesselSituation = captured.VesselSituation;
            rec.DistanceFromLaunch = captured.DistanceFromLaunch;
            rec.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;
            rec.PreLaunchFunds = captured.PreLaunchFunds;
            rec.PreLaunchScience = captured.PreLaunchScience;
            rec.PreLaunchReputation = captured.PreLaunchReputation;
            rec.RewindSaveFileName = captured.RewindSaveFileName;
            rec.RewindReservedFunds = captured.RewindReservedFunds;
            rec.RewindReservedScience = captured.RewindReservedScience;
            rec.RewindReservedRep = captured.RewindReservedRep;
            rec.CopyStartLocationFrom(captured);
            rec.EndBiome = captured.EndBiome;
            rec.Controllers = captured.Controllers != null
                ? new List<ControllerInfo>(captured.Controllers)
                : null;

            if (captured.VesselDestroyed)
            {
                rec.StampTerminalState(TerminalState.Destroyed, "FallbackCommitSplitRecorder");
                ParsekLog.Verbose("Flight", "FallbackCommitSplitRecorder: set TerminalState=Destroyed");
            }
        }

        /// <summary>
        /// The recorded vessel pid for a standalone fallback recording: the recorder's own
        /// <c>RecordingVesselId</c> when known, else the capture snapshot's top-level
        /// <c>persistentId</c>, else 0.
        /// </summary>
        internal static uint ResolveStandaloneRecordedVesselPid(uint recordedVesselPid, Recording captured)
        {
            if (recordedVesselPid != 0)
                return recordedVesselPid;
            if (captured?.VesselSnapshot == null)
                return 0u;
            uint snapshotPid;
            string raw = captured.VesselSnapshot.GetValue("persistentId");
            if (!string.IsNullOrEmpty(raw)
                && uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshotPid))
                return snapshotPid;
            return 0u;
        }

        /// <summary>
        /// Always-tree mode: every recorder is bound to a tree (<c>StartRecording</c> wraps a
        /// lone recording in a single-node tree and every resume sets
        /// <c>recorder.ActiveTree</c>). A stopped capture whose recorder was bound to a tree
        /// while NO tree is active belongs to a tree the scene already dropped. Every
        /// tree-drop site except <c>ResetFlightReadyState</c> drops the recorder with it;
        /// that one keeps a stopped recorder, and the next <c>StartRecording</c> resolves its
        /// capture here. A standalone commit of it is never written by OnSave (only
        /// RECORDING_TREE nodes are saved), so it would live until the next cold load only
        /// while replaying the dropped session's tail: after a Rewind-to-Launch that kept
        /// the vessel it records, its terminal spawn would build a second copy of that
        /// vessel on the same spot. Returns true (and logs) when the capture must be
        /// discarded instead of committed.
        /// </summary>
        internal static bool TryDiscardCaptureOfDroppedTree(
            RecordingTree liveActiveTree,
            RecordingTree recorderTree,
            Recording captured,
            uint recordedVesselPid)
        {
            if (liveActiveTree != null || recorderTree == null)
                return false;

            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "FallbackCommitSplitRecorder: discarded capture of dropped tree '{0}' (id={1}) " +
                "vessel='{2}' pid={3} points={4} - no standalone commit outside a tree",
                recorderTree.TreeName ?? "<unnamed>",
                recorderTree.Id ?? "<no-id>",
                captured?.VesselName ?? "<unnamed>",
                recordedVesselPid,
                captured?.Points?.Count ?? 0));
            return true;
        }

        /// <summary>
        /// Tags the recording's SegmentPhase and SegmentBodyName based on the vessel's current
        /// body and altitude, if not already set. No-op if SegmentPhase is already non-empty
        /// or vessel/mainBody is null.
        /// </summary>
        internal static void TagSegmentPhaseIfMissing(Recording pending, Vessel v)
        {
            if (pending != null
                && string.IsNullOrEmpty(pending.SegmentPhase)
                && SegmentPhaseClassifier.TryClassify(v, out string phase, out string bodyName))
            {
                pending.SegmentBodyName = bodyName;
                pending.SegmentPhase = phase;
            }
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and two child Recording objects
        /// for a vessel split. Testable without Unity.
        /// For EVA branches, evaVesselPid identifies which child is the kerbal (receives
        /// EvaCrewName and ParentRecordingId). If 0, defaults to activeVesselPid.
        /// </summary>
        internal static (BranchPoint bp, Recording activeChild, Recording backgroundChild)
            BuildSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint activeVesselPid, string activeVesselName,
                uint backgroundVesselPid, string backgroundVesselName,
                string evaCrewName = null, uint evaVesselPid = 0,
                int parentGeneration = 0)
        {
            string activeChildId = Guid.NewGuid().ToString("N");
            string backgroundChildId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { activeChildId, backgroundChildId }
            };

            // activeChild continues the same logical vessel — keep parent generation.
            // backgroundChild is the new spinoff — gets parentGeneration + 1.
            var activeChild = new Recording
            {
                RecordingId = activeChildId,
                TreeId = treeId,
                VesselPersistentId = activeVesselPid,
                VesselName = activeVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT,
                Generation = parentGeneration
            };

            var backgroundChild = new Recording
            {
                RecordingId = backgroundChildId,
                TreeId = treeId,
                VesselPersistentId = backgroundVesselPid,
                VesselName = backgroundVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT,
                Generation = parentGeneration + 1
            };

            // For EVA: kerbal child gets EvaCrewName and ParentRecordingId.
            // Identify kerbal by PID match (handles both active and background kerbal cases).
            if (branchType == BranchPointType.EVA && !string.IsNullOrEmpty(evaCrewName))
            {
                uint kerbalPid = evaVesselPid != 0 ? evaVesselPid : activeVesselPid;
                if (kerbalPid == activeVesselPid)
                {
                    activeChild.EvaCrewName = evaCrewName;
                    activeChild.ParentRecordingId = backgroundChildId;
                }
                else
                {
                    backgroundChild.EvaCrewName = evaCrewName;
                    backgroundChild.ParentRecordingId = activeChildId;
                }
            }

            return (bp, activeChild, backgroundChild);
        }

        internal static SegmentEnvironment? GetEvaBackgroundInitialEnvironmentOverride(
            BranchPointType branchType,
            bool backgroundChildIsEva,
            int activeSituation,
            double backgroundSrfSpeed)
        {
            if (branchType != BranchPointType.EVA || !backgroundChildIsEva)
                return null;

            if (activeSituation == (int)Vessel.Situations.LANDED ||
                activeSituation == (int)Vessel.Situations.SPLASHED ||
                activeSituation == (int)Vessel.Situations.PRELAUNCH)
            {
                return backgroundSrfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            return null;
        }

        internal static bool TryResolveTrackedBackgroundParentRecording(
            RecordingTree tree,
            uint sourceVesselPid,
            out string recordingId,
            out string diagnostic)
        {
            recordingId = null;

            if (tree == null)
            {
                diagnostic = "tree-null";
                return false;
            }

            if (sourceVesselPid == 0)
            {
                diagnostic = "source-pid-zero";
                return false;
            }

            if (tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(sourceVesselPid, out recordingId))
            {
                diagnostic = "background-map-hit";
                return true;
            }

            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                tree.RebuildBackgroundMap();
                if (tree.BackgroundMap != null
                    && tree.BackgroundMap.TryGetValue(sourceVesselPid, out recordingId))
                {
                    diagnostic = "background-map-hit-after-rebuild";
                    return true;
                }

                diagnostic = "background-map-miss-after-rebuild";
                return false;
            }

            diagnostic = "background-map-miss-active-head-present";
            return false;
        }

        internal static bool CanStartRecorderWithActiveTreeHead(
            RecordingTree tree,
            uint activeVesselPid,
            out string reason)
        {
            reason = null;

            if (tree == null)
                return true;

            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                reason = "active-recording-id-missing";
                return false;
            }

            Recording activeRecording;
            if (tree.Recordings == null
                || !tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRecording)
                || activeRecording == null)
            {
                reason = $"active-recording-not-found:{tree.ActiveRecordingId}";
                return false;
            }

            uint activeRecordingPid = ResolveLiveTreeRecordingPidForRestore(activeRecording);
            if (activeVesselPid != 0 && activeRecordingPid != 0 && activeRecordingPid != activeVesselPid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "active-recording-pid-mismatch:{0}!={1}",
                    activeRecordingPid,
                    activeVesselPid);
                return false;
            }

            return true;
        }

        internal static EvaBackgroundParentRouteDecision DecideEvaBackgroundParentRoute(
            bool isRecording,
            bool pendingSplitInProgress,
            bool hasActiveTree,
            bool hasSourceVessel,
            bool trackedParentResolved,
            bool mappedRecordingExists,
            bool hasCrewName)
        {
            if (isRecording || !hasActiveTree || !hasSourceVessel)
                return EvaBackgroundParentRouteDecision.NotHandled;

            if (pendingSplitInProgress)
                return EvaBackgroundParentRouteDecision.HandledSkipPendingSplit;

            if (!trackedParentResolved)
                return EvaBackgroundParentRouteDecision.HandledUntrackedSource;

            if (!mappedRecordingExists)
                return EvaBackgroundParentRouteDecision.HandledInvalidMapEntry;

            if (!hasCrewName)
                return EvaBackgroundParentRouteDecision.HandledMissingCrewName;

            return EvaBackgroundParentRouteDecision.StartDeferredBackgroundParentBranch;
        }

        internal static EvaBackgroundParentFocusDecision DecideEvaBackgroundParentFocus(
            uint currentActivePid,
            uint evaPid,
            uint sourceVesselPid)
        {
            if (currentActivePid != 0 && evaPid != 0 && currentActivePid == evaPid)
                return EvaBackgroundParentFocusDecision.EvaKerbalActive;
            if (currentActivePid != 0 && sourceVesselPid != 0 && currentActivePid == sourceVesselPid)
                return EvaBackgroundParentFocusDecision.SourceVesselActive;
            return EvaBackgroundParentFocusDecision.Invalid;
        }

        /// <summary>
        /// Decides whether an EVA branch recording that is still parked in the tree's
        /// BackgroundMap must be promoted to the live recorder at the moment its kerbal
        /// boards a vessel, so the board merge can run.
        /// </summary>
        /// <remarks>
        /// Ordering matters. The live wrapper runs inside <c>GameEvents.onCrewBoardVessel</c>,
        /// which KSP fires BEFORE <c>FlightGlobals.SetActiveVessel</c>, so the EVA kerbal is
        /// still the active vessel; <see cref="FlightRecorder.StartRecording"/> binds to
        /// <c>FlightGlobals.ActiveVessel</c>, which is why
        /// <paramref name="activeVesselPid"/> must still equal the EVA kerbal.
        /// </remarks>
        internal static EvaBoardPromotionDecision DecideEvaBoardPromotion(
            bool hasActiveTree,
            bool restoringActiveTree,
            bool sourceIsEva,
            bool sourceIsGhostVessel,
            uint sourceVesselPid,
            bool hasLiveRecorder,
            uint liveRecorderVesselPid,
            uint activeVesselPid,
            bool sourceTrackedInBackground,
            string trackedRecordingId,
            string treeActiveRecordingId)
        {
            if (!hasActiveTree)
                return EvaBoardPromotionDecision.SkipNoTree;

            if (restoringActiveTree)
                return EvaBoardPromotionDecision.SkipRestoring;

            if (sourceVesselPid == 0 || !sourceIsEva)
                return EvaBoardPromotionDecision.SkipNotEva;

            if (sourceIsGhostVessel)
                return EvaBoardPromotionDecision.SkipGhostVessel;

            // Green path: the first-modification watcher already promoted this EVA
            // recording (or it never left the live recorder). Leave it alone.
            if (hasLiveRecorder && liveRecorderVesselPid == sourceVesselPid)
                return EvaBoardPromotionDecision.NotNeeded;

            // A live recorder on some OTHER vessel owns the tree's active recording;
            // stealing it here would corrupt that recording's continuity.
            if (hasLiveRecorder)
                return EvaBoardPromotionDecision.SkipRecorderBusy;

            if (activeVesselPid != sourceVesselPid)
                return EvaBoardPromotionDecision.SkipEvaNotActiveVessel;

            if (!sourceTrackedInBackground)
                return EvaBoardPromotionDecision.SkipNoTrackedRecording;

            // The tree still names another recording as active while no live recorder owns it.
            // On the normal EVA-board path this is null: both OnVesselSwitchComplete
            // backgrounding branches park the old recording into BackgroundMap and clear
            // ActiveRecordingId before the kerbal ever boards. A non-null, different id here
            // means an earlier start failed midway (CreateMergeBranch step 10 nulls the recorder
            // but keeps its ActiveRecordingId), and PromoteRecordingFromBackground would
            // overwrite that id without parking it back into BackgroundMap - orphaning it.
            // Fail closed: skip the promotion (the board merge is then skipped and logged)
            // rather than trade one dropped merge for a lost recording.
            if (!string.IsNullOrEmpty(treeActiveRecordingId)
                && !string.Equals(treeActiveRecordingId, trackedRecordingId, StringComparison.Ordinal))
            {
                return EvaBoardPromotionDecision.SkipTreeActiveRecordingBusy;
            }

            return EvaBoardPromotionDecision.Promote;
        }

        internal static string BuildInvalidActiveTreeHeadRateLimitKey(
            string source,
            RecordingTree tree,
            uint activeVesselPid,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}-invalid-active-tree-head-tree={1}-activeRec={2}-activePid={3}-reason={4}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(tree?.Id) ? "none" : tree.Id,
                string.IsNullOrEmpty(tree?.ActiveRecordingId) ? "none" : tree.ActiveRecordingId,
                activeVesselPid,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason);
        }

        internal struct BackgroundMapEntrySnapshot
        {
            public uint VesselPid;
            public bool HadEntry;
            public string RecordingId;
        }

        internal static List<BackgroundMapEntrySnapshot> CaptureBackgroundMapEntries(
            RecordingTree tree,
            params uint[] vesselPids)
        {
            var snapshots = new List<BackgroundMapEntrySnapshot>();
            if (tree?.BackgroundMap == null || vesselPids == null)
                return snapshots;

            var seen = new HashSet<uint>();
            for (int i = 0; i < vesselPids.Length; i++)
            {
                uint pid = vesselPids[i];
                if (pid == 0 || !seen.Add(pid))
                    continue;

                string recordingId;
                bool hadEntry = tree.BackgroundMap.TryGetValue(pid, out recordingId);
                snapshots.Add(new BackgroundMapEntrySnapshot
                {
                    VesselPid = pid,
                    HadEntry = hadEntry,
                    RecordingId = recordingId
                });
            }

            return snapshots;
        }

        internal static void RestoreBackgroundMapEntries(
            RecordingTree tree,
            List<BackgroundMapEntrySnapshot> snapshots)
        {
            if (tree?.BackgroundMap == null || snapshots == null)
                return;

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot.VesselPid == 0)
                    continue;

                if (snapshot.HadEntry)
                    tree.BackgroundMap[snapshot.VesselPid] = snapshot.RecordingId;
                else
                    tree.BackgroundMap.Remove(snapshot.VesselPid);
            }
        }

        internal static void RollbackBackgroundParentBranchMutation(
            RecordingTree tree,
            string previousActiveRecordingId,
            string parentRecordingId,
            string previousParentChildBranchPointId,
            BranchPoint bp,
            Recording activeChild,
            Recording backgroundChild,
            List<BackgroundMapEntrySnapshot> previousBackgroundMapEntries)
        {
            if (tree == null)
                return;

            tree.ActiveRecordingId = previousActiveRecordingId;

            if (!string.IsNullOrEmpty(parentRecordingId)
                && tree.Recordings.TryGetValue(parentRecordingId, out Recording parentRecording)
                && parentRecording != null)
            {
                parentRecording.ChildBranchPointId = previousParentChildBranchPointId;
            }

            if (bp != null && !string.IsNullOrEmpty(bp.Id))
                tree.BranchPoints.RemoveAll(candidate => candidate != null && candidate.Id == bp.Id);

            if (activeChild != null && !string.IsNullOrEmpty(activeChild.RecordingId))
                tree.Recordings.Remove(activeChild.RecordingId);
            if (backgroundChild != null && !string.IsNullOrEmpty(backgroundChild.RecordingId))
                tree.Recordings.Remove(backgroundChild.RecordingId);

            RestoreBackgroundMapEntries(tree, previousBackgroundMapEntries);
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and one child Recording object
        /// for a vessel merge (dock or board). Testable without Unity.
        /// Takes a list of parent recording IDs (1 for foreign vessel merge, 2 for two-tree merge)
        /// and returns a single child recording for the merged vessel.
        /// </summary>
        internal static (BranchPoint bp, Recording mergedChild)
            BuildMergeBranchData(
                List<string> parentRecordingIds, string treeId, double mergeUT,
                BranchPointType branchType, uint mergedVesselPid, string mergedVesselName,
                uint targetVesselPersistentId = 0,
                RouteConnectionKind transferKind = RouteConnectionKind.None,
                uint branchPartnerPid = 0,
                string branchPartnerGuid = null)
        {
            string childId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");
            uint routeTargetPid = targetVesselPersistentId;
            // Branch stamp decoupling (design-dock-event-graph.md 6.1): the branch
            // point carries the UNGATED couple-event partner identity when the caller
            // supplies one; legacy callers that pass only the gated route pid keep the
            // historical coupling (fallback). Route surfaces below read routeTargetPid
            // exclusively, so a stamp without route eligibility never invents proof.
            uint branchTargetPid = branchPartnerPid != 0
                ? branchPartnerPid : targetVesselPersistentId;

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = mergeUT,
                Type = branchType,
                ParentRecordingIds = new List<string>(parentRecordingIds),
                ChildRecordingIds = new List<string> { childId },
                MergeCause = GetMergeCauseForBranchType(branchType),
                TargetVesselPersistentId = branchTargetPid,
                // The guid names the same vessel as the pid, so it never outlives a zero pid.
                TargetVesselGuid = branchTargetPid != 0 && !string.IsNullOrEmpty(branchPartnerGuid)
                    ? branchPartnerGuid : null
            };

            var mergedChild = new Recording
            {
                RecordingId = childId,
                TreeId = treeId,
                VesselPersistentId = mergedVesselPid,
                VesselName = mergedVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = mergeUT,
                TransferTargetVesselPid = branchType == BranchPointType.Dock ? routeTargetPid : 0,
                TransferKind = branchType == BranchPointType.Dock && routeTargetPid != 0
                    ? (transferKind != RouteConnectionKind.None ? transferKind : RouteConnectionKind.DockingPort)
                    : RouteConnectionKind.None
            };

            return (bp, mergedChild);
        }

        /// <summary>
        /// Claw producer (design-logistics-claw-producer.md 2.1): an EVA kerbal
        /// grab fires a real Part.Couple (findings 1.4) but is never a logistics
        /// transfer, so its route window is suppressed at capture by dropping
        /// the route target. Shared by both OnPartCouple paths (live and
        /// retroactive); pure except for the Info log, so unit tests pin the
        /// decision through the log sink.
        /// </summary>
        internal static uint SuppressRouteWindowForEvaGrab(
            uint routeTargetPid,
            bool involvesEva,
            RouteConnectionKind kind,
            string pathLabel)
        {
            if (!involvesEva || routeTargetPid == 0u)
                return routeTargetPid;
            ParsekLog.Info("Flight",
                $"OnPartCouple{pathLabel}: EVA grab, route window suppressed " +
                $"(kind={kind}, partnerPid={routeTargetPid})");
            return 0u;
        }

        /// <summary>
        /// The branch-point partner stamp: the couple-event partner identity, decoupled
        /// from route eligibility (design-dock-event-graph.md 6.1). Applies the same
        /// self/zero filtering as the route pid but NOT the snapshot/known-recording
        /// disjunct, so a route-ineligible dock still records who it docked with
        /// (derivable retroactively once the partner's tree commits).
        ///
        /// <para>
        /// The EVA suppression keys on the PARTNER side only
        /// (<paramref name="partnerIsEva"/>, from
        /// <see cref="ConnectionProducerClassifier.PartnerIsEvaVessel"/>): a claw grabbing
        /// a kerbal must not mint a claim keyed on the kerbal's pid, but a kerbal taking a
        /// stock external command seat (<c>KerbalEVA</c> <c>On_seatBoard</c> couples the
        /// kerbal's own part onto the seat part) is an EVA recorder merging into the
        /// partner vessel, and that vessel's stamp is the claim the chain walker and the
        /// rewind hold read. Pure; both OnPartCouple paths log the result on their
        /// resolve line.
        /// </para>
        /// </summary>
        internal static uint ResolveBranchPartnerStampPid(
            uint partnerPidFromEvent, uint selfVesselPid, bool partnerIsEva)
        {
            if (partnerIsEva || partnerPidFromEvent == 0u
                || partnerPidFromEvent == selfVesselPid)
                return 0u;
            return partnerPidFromEvent;
        }

        /// <summary>
        /// The branch-point partner stamp of a Board merge: the boarded vessel's pid and live
        /// launch guid when that vessel is outside the tree (another mission's vessel, or one
        /// no recording carries), so the chain walker and the rewind hold see the boarding as
        /// a claim on it, as they do a Dock onto it. A board onto a vessel of the same tree
        /// (its <c>BackgroundMap</c> holds it) is a two-parent merge that already closes that
        /// vessel's own line, so it stays unstamped. Pure.
        /// </summary>
        internal static uint ResolveBoardPartnerStamp(
            bool targetInTree, uint boardedVesselPid, string boardedVesselGuid, out string partnerGuid)
        {
            partnerGuid = null;
            if (targetInTree || boardedVesselPid == 0u)
                return 0u;
            partnerGuid = string.IsNullOrEmpty(boardedVesselGuid) ? null : boardedVesselGuid;
            return boardedVesselPid;
        }

        /// <summary>
        /// The launch guid to stamp beside the branch-point partner pid: the live
        /// <c>Vessel.id</c> of whichever couple-event side carries
        /// <paramref name="partnerStampPid"/>, read while both vessels still exist. The pid
        /// alone is craft-baked and freed when the partner is absorbed, so a relaunch of the
        /// same craft can reuse it; the guid keeps the chain walker and the cross-tree dock
        /// links from pooling the two launches when no recording in the claiming tree
        /// carries the partner's pid. Null when the stamp is 0 or neither side matches /
        /// has an id (the consumers then keep the pid-only resolution). Pure.
        /// </summary>
        internal static string ResolveDockPartnerGuidFromEvent(
            uint partnerStampPid, uint fromVesselPid, string fromVesselGuid,
            uint toVesselPid, string toVesselGuid)
        {
            if (partnerStampPid == 0u)
                return null;
            if (fromVesselPid == partnerStampPid && !string.IsNullOrEmpty(fromVesselGuid))
                return fromVesselGuid;
            if (toVesselPid == partnerStampPid && !string.IsNullOrEmpty(toVesselGuid))
                return toVesselGuid;
            return null;
        }

        /// <summary>
        /// PHANTOM-SUPERSEDE-RIDES-GATED-PID: resolves the pid whose committed terminal
        /// spawn a dock merge must suppress, returning 0 when the suppression does not
        /// apply.
        ///
        /// <para>
        /// The absorbed-vessel spawn suppression is a PARTNER-IDENTITY concern, not a
        /// route concern, so it reads the branch point's ungated couple-event partner
        /// stamp (<see cref="BranchPoint.TargetVesselPersistentId"/>, produced by
        /// <see cref="BuildMergeBranchData"/> from
        /// <see cref="ResolveBranchPartnerStampPid"/> with a legacy fallback to the gated
        /// route pid) rather than the route-eligibility-gated
        /// <c>routeTargetVesselPid</c>.
        /// </para>
        ///
        /// <para>
        /// <b>The newly-admitted population, stated precisely</b> — route eligibility is
        /// a DISJUNCTION (<c>pid resolvable &amp;&amp; (partnerSnapshotCaptured ||
        /// partnerKnown)</c>, both <c>OnPartCouple</c> paths), and
        /// <c>partnerSnapshotCaptured</c> is true for any partner still loaded with parts
        /// at couple time. So an ordinary physically-docking Parsek spawn WAS already
        /// eligible; "Parsek spawns were route-unknown" is too broad a claim and is not
        /// the mechanism. What the gated pid actually loses is the dock where the pid
        /// resolves but BOTH disjuncts fail: no usable pre-couple snapshot (KSP already
        /// reparented <c>data.from.vessel</c> onto <c>data.to.vessel</c> — the retroactive
        /// path's own §5.1 note — or the partner has no parts, or
        /// <c>FlightRecorder.TryBackupSnapshot</c> returned null) AND
        /// <c>IsKnownDockPartnerForRoute</c> finds nothing, since it compares ONLY against
        /// <see cref="Recording.VesselPersistentId"/> and a Parsek-spawned vessel is live
        /// under its KSP-unique <c>SpawnedVesselPersistentId</c>. That narrow set is
        /// nonetheless exactly what
        /// <see cref="TreeCommitService.MarkTerminalSpawnSupersededByDockMerge"/> matches on
        /// its spawn-pid route, so it is real phantom exposure — the change is a strict
        /// widening, never a re-aim.
        /// </para>
        ///
        /// <para>
        /// <b>Read the guid gate correctly for that population.</b> The
        /// <c>absorbedLaunchGuid</c> the call site passes has exactly one source: the
        /// pre-couple partner snapshot. For a newly-admitted dock that snapshot disjunct
        /// was false by construction, so the guid is ALWAYS null there and the
        /// <c>GuidsConclusivelyDiffer</c> gate is inert on 100% of the new population.
        /// Safety comes from a different fact: <c>!partnerKnown</c> means no committed
        /// recording carries <c>VesselPersistentId == absorbedPid</c>, so the guid-gated
        /// <c>bakedPidMatch</c> route cannot fire at all — only the collision-free
        /// <c>uniqueSpawnMatch</c> route can, and CLAUDE.md's identity rule sanctions that
        /// one as pid-only precisely because a KSP-unique spawn pid cannot collide.
        /// </para>
        ///
        /// <para>
        /// The EVA-grab carve-out survives the switch: both
        /// <see cref="ResolveBranchPartnerStampPid"/> (when the partner is the EVA kerbal)
        /// and <see cref="SuppressRouteWindowForEvaGrab"/> zero their pid, so a claw grab of
        /// a kerbal still suppresses nothing. A kerbal taking an external seat stamps the
        /// seat's vessel, which survives as the merged vessel, so it suppresses nothing
        /// either (the merged-pid check below).
        /// </para>
        /// </summary>
        internal static uint ResolveDockMergeSpawnSuppressionPid(
            BranchPointType branchType, uint branchStampPid, uint mergedVesselPid)
        {
            // Dock-only: boarding does not absorb a separate spawned / adopted committed
            // leaf (a foreign Board's stamp names the vessel that flies on as the merged child).
            if (branchType != BranchPointType.Dock) return 0u;
            // 0 = no resolvable partner; == merged means the partner survived AS the
            // merged vessel, so its terminal spawn is owned by the live continuation.
            if (branchStampPid == 0u || branchStampPid == mergedVesselPid) return 0u;
            return branchStampPid;
        }

        internal static uint ResolveDockRouteTargetPid(
            bool activeWasDockTarget,
            uint mergedVesselPid,
            uint absorbedVesselPid)
        {
            // Returns the docking partner's persistentId -- the OTHER vessel from the
            // active recording's point of view. When the active recording survived as
            // the dock target (active.pid == mergedVesselPid), the partner is the
            // absorbed vessel; when the active recording was absorbed, the partner is
            // the surviving merged vessel. Runtime coverage must verify both paths
            // before route creation consumes this as endpoint proof.
            return activeWasDockTarget ? absorbedVesselPid : mergedVesselPid;
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

        internal static bool TryBuildRouteEndpointFromSnapshot(
            ConfigNode snapshot,
            uint expectedVesselPersistentId,
            out RouteEndpoint endpoint,
            out int endpointSituation)
        {
            endpoint = default(RouteEndpoint);
            endpointSituation = -1;

            if (snapshot == null || expectedVesselPersistentId == 0)
                return false;

            string pidStr = snapshot.GetValue("persistentId");
            if (pidStr == null ||
                !uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid) ||
                pid != expectedVesselPersistentId)
            {
                return false;
            }

            if (!TryParseRouteEndpointSituation(snapshot, out Vessel.Situations situation))
                return false;

            if (!TryGetSnapshotRouteCoordinate(snapshot, "lat", "latitude", out double latitude) ||
                !TryGetSnapshotRouteCoordinate(snapshot, "lon", "longitude", out double longitude) ||
                !TryGetSnapshotRouteCoordinate(snapshot, "alt", "altitude", out double altitude))
            {
                return false;
            }

            LiveVesselSnapshot.TryGetSnapshotReferenceBodyName(snapshot, out string bodyName);

            // Same stamp from the SNAPSHOT path: the background parent's own VESSEL node is
            // a pre-couple record of the endpoint, so its `root` index names the launch-unique
            // part flightID. An unreadable root leaves 0, which degrades this endpoint to the
            // pid + proximity walk it always had rather than refusing it.
            VesselSnapshotOps.TryReadRootPartFlightId(snapshot, out uint snapshotRootPartUId);

            endpoint = new RouteEndpoint
            {
                VesselPersistentId = pid,
                RootPartUId = snapshotRootPartUId,
                BodyName = bodyName,
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                IsSurface = IsRouteEndpointSurfaceSituation(situation)
            };
            endpointSituation = (int)situation;
            return true;
        }

        private static bool TryParseRouteEndpointSituation(
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

        private static bool TryGetSnapshotRouteCoordinate(
            ConfigNode snapshot,
            string primaryKey,
            string fallbackKey,
            out double value)
        {
            return VesselSnapshotOps.TryGetSnapshotDouble(snapshot, primaryKey, out value)
                || VesselSnapshotOps.TryGetSnapshotDouble(snapshot, fallbackKey, out value);
        }

        private static bool IsRouteEndpointSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED ||
                situation == Vessel.Situations.SPLASHED ||
                situation == Vessel.Situations.PRELAUNCH;
        }

        private void CapturePendingDockRouteEndpointProof(
            GameEvents.FromToAction<Part, Part> data,
            uint routeTargetPid)
        {
            pendingDockRouteEndpointAtDock = null;
            pendingDockRouteEndpointSituation = -1;

            if (routeTargetPid == 0)
                return;

            Vessel endpointVessel = ResolveDockRouteEndpointVesselFromEvent(data, routeTargetPid);
            RouteEndpoint? endpoint = BuildRouteEndpointFromVessel(endpointVessel, routeTargetPid);
            if (endpoint.HasValue)
            {
                pendingDockRouteEndpointAtDock = endpoint;
                pendingDockRouteEndpointSituation = endpointVessel != null
                    ? (int)endpointVessel.situation
                    : -1;
                ParsekLog.Verbose("Flight",
                    $"CapturePendingDockRouteEndpointProof: captured endpoint proof targetPid={routeTargetPid} " +
                    $"situation={pendingDockRouteEndpointSituation}");
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

        private static string GetMergeCauseForBranchType(BranchPointType branchType)
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
        void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0,
            TrajectoryPoint? backgroundInitialTrajectoryPoint = null)
        {
            RecorderStateLog.RecState("CreateSplitBranch:entry", CaptureRecorderState());
            if (pendingSplitRecorder == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no pending split recorder — aborting");
                return;
            }

            var splitRecorder = pendingSplitRecorder;

            // Use the current active recording as parent
            string parentRecordingId = activeTree.ActiveRecordingId;

            if (parentRecordingId == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no active recording in existing tree — aborting");
                FallbackCommitSplitRecorder(splitRecorder);
                return;
            }

            // Flush captured data into the existing tree recording
            Recording parentRec;
            if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                AppendCapturedDataToRecording(parentRec, splitRecorder.CaptureAtStop, branchUT);

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
                CopyRewindSaveToRoot(activeTree, splitRecorder);
            }

            // Set ChildBranchPointId on parent
            Recording parentRecording;
            if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording))
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
            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, branchType,
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
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(activeChild);
            activeTree.AddOrReplaceRecording(bgChild);

            // Set active recording
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // Add background child to BackgroundMap FIRST, then notify BackgroundRecorder
            if (backgroundVessel != null && backgroundVessel.persistentId != 0)
            {
                var initialBackgroundEnvOverride = GetEvaBackgroundInitialEnvironmentOverride(
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

                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
                backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride,
                    initialTrajectoryPoint: backgroundInitialTrajectoryPoint);
            }

            // Create new FlightRecorder for active child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"CreateSplitBranch: StartRecording failed for active child");
                recorder = null;
            }
            else
            {
                PrepareSessionStateForRecorderStart("CreateSplitBranch");
            }

            ParsekLog.Info("Flight", $"Tree branch created: type={branchType}, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId})" +
                (backgroundInitialTrajectoryPoint.HasValue ? ", bgInitialSeed=part-origin" : "") +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranch:exit", CaptureRecorderState());

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
                activeTree, parentRec, out predecessorRec, out predecessorReason);
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

        private void CreateSplitBranchFromBackgroundParent(
            string parentRecordingId,
            uint sourceVesselPid,
            Vessel activeVessel,
            Vessel backgroundVessel,
            double branchUT,
            string evaCrewName,
            uint evaVesselPid,
            string branchPath)
        {
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:entry", CaptureRecorderState());

            if (activeTree == null)
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
            if (!activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording) || parentRecording == null)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' not found - aborting");
                return;
            }

            if (BackgroundParentAlreadySplit(parentRecording, activeTree))
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
                || !activeTree.BackgroundMap.TryGetValue(sourceVesselPid, out mappedParentRecordingId)
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

            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, BranchPointType.EVA,
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

            string previousActiveRecordingId = activeTree.ActiveRecordingId;
            string previousParentChildBranchPointId = parentRecording.ChildBranchPointId;
            var previousBackgroundMapEntries = CaptureBackgroundMapEntries(
                activeTree,
                sourceVesselPid,
                activeVessel.persistentId,
                backgroundVessel.persistentId);

            parentRecording.ChildBranchPointId = bp.Id;
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(activeChild);
            activeTree.AddOrReplaceRecording(bgChild);
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // The source vessel is now either the active or background child, depending on KSP focus.
            activeTree.BackgroundMap.Remove(activeVessel.persistentId);
            activeTree.BackgroundMap.Remove(backgroundVessel.persistentId);
            SegmentEnvironment? initialBackgroundEnvOverride = null;
            if (backgroundVessel.persistentId != 0)
            {
                initialBackgroundEnvOverride = GetEvaBackgroundInitialEnvironmentOverride(
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

                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
            }

            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: StartRecording failed for active child " +
                    $"pid={activeChild.VesselPersistentId}");
                recorder = null;
                RollbackBackgroundParentBranchMutation(
                    activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            if (recorder.RecordingVesselId != activeChild.VesselPersistentId)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: recorder vessel mismatch after start " +
                    $"(recorderPid={recorder.RecordingVesselId}, activeChildPid={activeChild.VesselPersistentId})");
                recorder.StopRecordingForChainBoundary();
                recorder = null;
                RollbackBackgroundParentBranchMutation(
                    activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            backgroundRecorder?.OnVesselRemovedFromBackground(sourceVesselPid);
            if (backgroundVessel.persistentId != 0)
            {
                backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride);
            }

            ClearPendingEvaAutoRecordState();
            PrepareSessionStateForRecorderStart("CreateSplitBranchFromBackgroundParent");

            ParsekLog.Info("Flight", $"Tree branch created: type=EVA, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId}), " +
                $"path={branchPath}" +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:exit", CaptureRecorderState());

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
        /// Pure helper: resolves the dock partner's vessel persistentId directly from
        /// the KSP couple event, independent of <c>activeTree.BackgroundMap</c>. Returns
        /// the side of the dock pair that is NOT the recorder's own vessel, or 0 when
        /// neither side qualifies. Used by the route-proof path so cross-tree partners
        /// (committed recording in a previous tree, not in the current
        /// <c>BackgroundMap</c>) still resolve correctly.
        /// </summary>
        internal static uint ResolveDockPartnerPidFromEvent(
            uint fromVesselPid,
            uint toVesselPid,
            uint selfVesselPid)
        {
            if (fromVesselPid != 0 && fromVesselPid != selfVesselPid) return fromVesselPid;
            if (toVesselPid != 0 && toVesselPid != selfVesselPid) return toVesselPid;
            return 0;
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
        /// Validation gate for route-eligibility of a dock partner. Returns true when
        /// the partner has a known Parsek recording — either an entry in the current
        /// <paramref name="activeTreeRecordings"/> (excluding the recorder's own
        /// vessel) or in <paramref name="committedRecordings"/>. Returns false for
        /// truly foreign vessels (KSP-visible but no Parsek recording), keeping them
        /// out of the route-proof path while still allowing the dock to merge normally.
        /// </summary>
        internal static bool IsKnownDockPartnerForRoute(
            uint partnerPid,
            uint selfVesselPid,
            IEnumerable<Recording> activeTreeRecordings,
            IReadOnlyList<Recording> committedRecordings)
        {
            if (partnerPid == 0 || partnerPid == selfVesselPid) return false;

            if (activeTreeRecordings != null)
            {
                foreach (Recording r in activeTreeRecordings)
                {
                    if (r != null && r.VesselPersistentId == partnerPid) return true;
                }
            }

            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording r = committedRecordings[i];
                    if (r != null && r.VesselPersistentId == partnerPid) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pure helper: the absorbed partner of a TARGET-side dock (our recorder's vessel
        /// survives as the merged vessel). The couple event names the partner directly
        /// (<paramref name="partnerPidFromEvent"/>); when that pid is a member of the active
        /// tree's <c>BackgroundMap</c> it is the absorbed same-tree vessel and becomes the
        /// merge's second parent. Otherwise the partner is foreign or cross-tree and the
        /// answer is 0 (single-parent merge). No shape heuristic over the map: onPartCouple
        /// fires BEFORE KSP merges the vessels, so the real partner still has all its parts,
        /// and a "vessel gone or partless" scan could only match a DIFFERENT member (an
        /// unloaded on-rails background stage reads zero parts) and dock it by mistake.
        /// </summary>
        internal static uint ResolveTargetSideAbsorbedPid(
            uint partnerPidFromEvent,
            uint mergedPid,
            ICollection<uint> backgroundMapPids)
        {
            if (partnerPidFromEvent != 0
                && partnerPidFromEvent != mergedPid
                && backgroundMapPids != null
                && backgroundMapPids.Contains(partnerPidFromEvent))
            {
                return partnerPidFromEvent;
            }
            return 0;
        }

        /// <summary>
        /// Cross-tree partner fallback for the dock-route window's
        /// <c>EndpointPartPersistentIds</c>. Tries (1) a live KSP vessel snapshot,
        /// (2) any matching recording in <c>activeTree.Recordings</c>, (3) any
        /// matching recording in <see cref="RecordingStore.CommittedRecordings"/>.
        /// Returns null when no usable snapshot can be located; callers preserve any
        /// existing list rather than overwriting it with null.
        /// </summary>
        private List<uint> TryDeriveEndpointPartPidsFromPartner(uint partnerPid)
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

            if (activeTree?.Recordings != null)
            {
                foreach (var kvp in activeTree.Recordings)
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
        /// Step 2 of <see cref="CreateMergeBranch"/>: flushes the stopped recorder's captured
        /// data into the active parent recording, stamps its Docked / Boarded terminal state,
        /// and copies the recorder's rewind save to the tree root. The rewind save lives only
        /// in CaptureAtStop once the recorder stops, and the merged child's recorder starts as
        /// a promotion without one, so a tree whose first boundary is a dock or board merge
        /// keeps its R button only through this copy (the split path copies at the same seam).
        /// Returns the parent recording, or null when the tree does not hold it.
        /// </summary>
        internal static Recording FlushStoppedRecorderIntoMergeParent(
            RecordingTree tree,
            string activeParentRecordingId,
            FlightRecorder stoppedRecorder,
            BranchPointType branchType,
            double mergeUT)
        {
            if (tree == null || string.IsNullOrEmpty(activeParentRecordingId)) return null;
            Recording activeParentRec;
            if (!tree.Recordings.TryGetValue(activeParentRecordingId, out activeParentRec))
            {
                ParsekLog.Warn("Flight",
                    $"CreateMergeBranch: active parent '{activeParentRecordingId}' not in tree " +
                    $"'{tree.Id}'; captured data not flushed");
                return null;
            }

            AppendCapturedDataToRecording(activeParentRec, stoppedRecorder?.CaptureAtStop, mergeUT);
            activeParentRec.StampTerminalState(
                (branchType == BranchPointType.Dock)
                    ? TerminalState.Docked : TerminalState.Boarded,
                "MergeBranch.activeParent");
            CopyRewindSaveToRoot(tree, stoppedRecorder, logTag: "CreateMergeBranch");
            return activeParentRec;
        }

        /// <summary>
        /// Creates a tree merge branch point for a dock or board event.
        /// Ends parent recordings, creates child recording for the merged vessel,
        /// and starts a new FlightRecorder for the child.
        /// Per Orchestrator Fix 3: absorbedVesselPid is NOT a parent-selection
        /// parameter -- background vessel PID is derived from the background parent
        /// recording. routeTargetVesselPid is only the route-proof endpoint PID.
        /// </summary>
        void CreateMergeBranch(
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
            Recording activeParentRec = FlushStoppedRecorderIntoMergeParent(
                activeTree, activeParentRecordingId, stoppedRecorder, branchType, mergeUT);

            // 3. End background parent recording (if two-parent merge)
            // Per Fix 3: derive background vessel PID from the recording's VesselPersistentId
            Recording bgParentRec = null;
            if (backgroundParentRecordingId != null)
            {
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
                {
                    bgParentRec.ExplicitEndUT = mergeUT;
                    bgParentRec.StampTerminalState(
                        (branchType == BranchPointType.Dock)
                            ? TerminalState.Docked : TerminalState.Boarded,
                        "MergeBranch.backgroundParent");

                    // Remove background vessel from BackgroundMap and notify BackgroundRecorder
                    uint bgVesselPid = bgParentRec.VesselPersistentId;
                    backgroundRecorder?.OnVesselRemovedFromBackground(bgVesselPid);
                    activeTree.BackgroundMap.Remove(bgVesselPid);
                }
            }

            // 4. Get merged vessel name
            Vessel mergedVessel = FlightRecorder.FindVesselByPid(mergedVesselPid);
            string mergedVesselName = Recording.ResolveLocalizedName(mergedVessel?.vesselName) ?? "Merged Vessel";

            // 5. Build merge branch data
            var (bp, mergedChild) = BuildMergeBranchData(
                parentIds, activeTree.Id, mergeUT, branchType,
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
                if (pendingDockPartnerSnapshot != null
                    && pendingDockPartnerSnapshotPid == routeTargetVesselPid)
                {
                    endpointPreCoupleSnapshot = pendingDockPartnerSnapshot;
                }

                // Pre-couple transport baseline captured in OnPartCouple (before the
                // same-frame stock crossfeed equalisation that the deferred merged
                // snapshot would otherwise bake in, deflating DOCK_TRANSPORT_RESOURCES and
                // tripping the MixedPickupDelivery gate). BuildDockRouteConnectionWindow
                // self-validates it against the transport PID set and falls back to the
                // merged snapshot when absent / mismatched, so this is unconditional.
                ConfigNode transportPreCoupleSnapshot = pendingDockSelfSnapshot;

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

                RouteEndpoint? endpointAtDock = pendingDockRouteEndpointAtDock;
                int endpointSituation = pendingDockRouteEndpointSituation;

                if ((!endpointAtDock.HasValue || endpointSituation < 0) &&
                    bgParentRec?.VesselSnapshot != null &&
                    bgParentRec.VesselPersistentId == routeTargetVesselPid &&
                    TryBuildRouteEndpointFromSnapshot(
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
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec2))
                    bgParentRec2.ChildBranchPointId = bp.Id;
            }

            // 8. Add to tree
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(mergedChild);

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
            uint spawnSuppressionPid = ResolveDockMergeSpawnSuppressionPid(
                branchType, bp.TargetVesselPersistentId, mergedVesselPid);
            if (spawnSuppressionPid != 0)
            {
                string absorbedLaunchGuid =
                    (pendingDockPartnerSnapshot != null
                     && pendingDockPartnerSnapshotPid == spawnSuppressionPid)
                        ? VesselLaunchIdentity.TryReadVesselGuid(pendingDockPartnerSnapshot)
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
            activeTree.ActiveRecordingId = mergedChild.RecordingId;

            // 10. Start new FlightRecorder for merged child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", "CreateMergeBranch: StartRecording failed for merged child");
                recorder = null;
            }
            else
            {
                PrepareSessionStateForRecorderStart("CreateMergeBranch");
            }

            // 11. Log
            ParsekLog.Info("Flight", $"Tree merge created: type={branchType}, " +
                $"bp={bp.Id}, parents=[{string.Join(",", parentIds)}], " +
                $"child={mergedChild.RecordingId} (pid={mergedVesselPid})");
        }

        /// <summary>
        /// Pure: the name a leg takes at its close. Only the RECORDED vessel itself may refresh
        /// it, and only on a positive identity match: the live vessel's pid equals the recorded
        /// pid AND both launch guids are known and equal. Otherwise (the vessel is gone, never
        /// resolved, or a guid is unknown or differs) the leg keeps <paramref name="existingName"/>.
        /// A name of another vessel (the stop-time active vessel) is never an input.
        /// <paramref name="source"/> is <c>recorded-vessel</c>, <c>kept-existing</c> or
        /// <c>none</c> (no live match and no existing name: the caller decides).
        /// </summary>
        internal static string ResolveRecordedVesselNameAtClose(
            string existingName,
            uint recordedPid,
            string recordedGuid,
            uint liveVesselPid,
            string liveVesselGuid,
            string liveVesselName,
            out string source)
        {
            string rg = VesselLaunchIdentity.NormalizeGuid(recordedGuid);
            string lg = VesselLaunchIdentity.NormalizeGuid(liveVesselGuid);
            bool positive = recordedPid != 0
                && liveVesselPid == recordedPid
                && rg != null && lg != null
                && string.Equals(rg, lg, StringComparison.OrdinalIgnoreCase);
            if (positive && !string.IsNullOrEmpty(liveVesselName))
            {
                source = "recorded-vessel";
                return liveVesselName;
            }
            if (!string.IsNullOrEmpty(existingName))
            {
                source = "kept-existing";
                return existingName;
            }
            source = "none";
            return null;
        }

        /// <summary>
        /// The name of a standalone fallback leg (<see cref="FallbackCommitSplitRecorder"/>):
        /// the recorded vessel's live name on a positive pid + guid match, else the name the
        /// recorded vessel had when this leg started (the start-of-leg ghost snapshot), else
        /// "Unknown Vessel". Logs the source.
        /// </summary>
        static string ResolveStandaloneFallbackLegName(FlightRecorder splitRec, Recording captured)
        {
            uint recordedPid = splitRec != null ? splitRec.RecordingVesselId : 0u;
            string startName = Recording.ResolveLocalizedName(
                captured?.GhostVisualSnapshot?.GetValue("name"));
            Vessel recordedVessel = recordedPid != 0 ? FlightRecorder.FindVesselByPid(recordedPid) : null;
            string name = ResolveRecordedVesselNameAtClose(
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
        void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            RecorderStateLog.RecState("FallbackCommitSplitRecorder:entry", CaptureRecorderState());
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;
            ApplyPendingSplitDestructionToCapturedRecording(
                captured,
                splitRec.VesselDestroyedDuringRecording,
                "FallbackCommitSplitRecorder.entry");

            // Bug #297: when in tree mode, append to the active tree recording
            // instead of orphaning data as a standalone recording.
            if (TryAppendCapturedToTree(activeTree, captured))
                return;

            if (TryDiscardCaptureOfDroppedTree(
                    activeTree, splitRec.ActiveTree, captured, splitRec.RecordingVesselId))
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
            ApplyCapturedSplitStateToStandaloneRecording(rec, captured, splitRec.RecordingVesselId);

            // Tag segment phase if untagged
            TagSegmentPhaseIfMissing(rec, FlightGlobals.ActiveVessel);

            // Destroyed vessels: discard pad failures or idle-on-pad, otherwise commit directly.
            // Both discards require pad evidence on the recording itself (this branch handles
            // a single recording): a one-sample recording has duration 0 and maxDist 0, so the
            // thresholds alone would discard an orbiting recording destroyed within one
            // sample interval.
            if (rec.VesselDestroyed)
            {
                double dur = rec.EndUT - rec.StartUT;
                double maxDist = rec.MaxDistanceFromLaunch;
                string discardReason = DecideDestroyedSplitRecordingDiscard(rec);
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
                    bool hasPadEvidence = HasPadEvidence(rec);
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
        void ResumeSplitRecorder(FlightRecorder splitRec, string reason, bool keepRecordedVessel = false)
        {
            RecorderStateLog.RecState("ResumeSplitRecorder:entry", CaptureRecorderState());
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
                recorder = splitRec;
                // Restore tree mode if tree is active
                if (activeTree != null)
                    recorder.ActiveTree = activeTree;
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
        bool CheckBranchDeduplication(double branchUT, uint newVesselPid)
        {
            if (Math.Abs(branchUT - lastBranchUT) > 0.1)
            {
                lastBranchVesselPids.Clear();
                lastBranchUT = branchUT;
            }
            if (lastBranchVesselPids.Contains(newVesselPid))
            {
                ParsekLog.Verbose("Flight", $"Skipping duplicate branch for pid={newVesselPid} at same UT");
                return false;
            }
            lastBranchVesselPids.Add(newVesselPid);
            return true;
        }

        IEnumerator DeferredUndockBranch(
            uint oldVesselPid,
            uint newVesselPid,
            TrajectoryPoint? backgroundInitialTrajectoryPoint = null)
        {
            yield return null; // defer one frame for KSP to finalize the split

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredUndockBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Resolve which side KSP gave focus to AFTER the one-frame yield. KSP keeps
                // focus on oldVessel for a normal undock, but if the player kept controlling
                // the pod that departed into the new vessel, KSP moves focus to it one frame
                // later (Vessel.WaitAndSwitchFocus). Mirror DeferredEvaBranch: the recorder
                // follows the focused side (active child), the OTHER side is backgrounded.
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                uint activePid = activeVessel.persistentId;
                uint backgroundPid = SegmentBoundaryLogic.ResolveUndockBackgroundPid(
                    activePid, oldVesselPid, newVesselPid);

                if (activePid != oldVesselPid && activePid != newVesselPid)
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: active vessel pid={activePid} is neither undock side " +
                        $"(old={oldVesselPid}, new={newVesselPid}); defaulting to background new, active stays current");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: active vessel pid={activePid} (old={oldVesselPid}, " +
                        $"new={newVesselPid}); backgrounding pid={backgroundPid}");
                }

                // Deduplication check keys on the backgrounded vessel (the new tree child).
                if (!CheckBranchDeduplication(branchUT, backgroundPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "undock dedup skip");
                    yield break;
                }

                // Find the vessel that goes to background.
                Vessel backgroundVessel = FlightRecorder.FindVesselByPid(backgroundPid);
                if (backgroundVessel == null)
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={backgroundPid} not found, debris destroyed, resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel not found");
                    yield break;
                }

                // Debris filter: a crewed vessel with no command module still gets its own
                // branch (ShouldRecordAsOwnBranch); only crewless commandless halves are debris.
                if (!ShouldRecordAsOwnBranch(backgroundVessel))
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: vessel pid={backgroundPid} is not trackable (debris), resuming recording " +
                        $"capturedSeed={backgroundInitialTrajectoryPoint.HasValue}");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel is debris");
                    yield break;
                }

                // Recorder follows the focused vessel (active child); the other goes to background.
                CreateSplitBranch(
                    BranchPointType.Undock,
                    activeVessel,
                    backgroundVessel,
                    branchUT,
                    backgroundInitialTrajectoryPoint: backgroundInitialTrajectoryPoint);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredEvaBranch(string kerbalName, GameEvents.FromToAction<Part, Part> evaData)
        {
            yield return null; // defer one frame for vessel switch to complete

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Get the EVA kerbal vessel and the ship vessel
                Vessel evaVessel = evaData.to?.vessel;
                Vessel shipVessel = evaData.from?.vessel;

                if (evaVessel == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: EVA vessel is null — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                uint evaPid = evaVessel.persistentId;
                if (!CheckBranchDeduplication(branchUT, evaPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "EVA dedup skip");
                    yield break;
                }

                // Determine which vessel is active by checking FlightGlobals.ActiveVessel
                Vessel currentActive = FlightGlobals.ActiveVessel;
                Vessel activeChild;
                Vessel backgroundChild;

                if (currentActive != null && currentActive.persistentId == evaPid)
                {
                    // KSP switched to the EVA kerbal (expected)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: EVA kerbal is active (expected path), evaPid={evaPid}");
                }
                else if (currentActive != null && shipVessel != null &&
                         currentActive.persistentId == shipVessel.persistentId)
                {
                    // KSP hasn't switched yet — ship is still active
                    activeChild = shipVessel;
                    backgroundChild = evaVessel;
                    // Note: tree vessel switch handling will sort out promotion when KSP switches
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: ship still active (KSP hasn't switched), shipPid={shipVessel.persistentId}");
                }
                else
                {
                    // Ambiguous — default to EVA kerbal as active (most common case)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: ambiguous active vessel, defaulting to EVA kerbal, activePid={currentActive?.persistentId}");
                }

                CreateSplitBranch(BranchPointType.EVA, activeChild, backgroundChild, branchUT, kerbalName, evaPid);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredEvaBranchFromBackgroundParent(
            string parentRecordingId,
            uint sourceVesselPid,
            string kerbalName,
            GameEvents.FromToAction<Part, Part> evaData)
        {
            yield return null; // defer one frame for vessel switch to complete

            try
            {
                if (activeTree == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranchFromBackgroundParent: no active tree after deferral - aborting");
                    yield break;
                }

                if (string.IsNullOrEmpty(parentRecordingId)
                    || !activeTree.Recordings.ContainsKey(parentRecordingId))
                {
                    ParsekLog.Warn("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: parent recording '{parentRecordingId ?? "null"}' " +
                        "is missing after deferral - aborting");
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();
                Vessel evaVessel = evaData.to?.vessel;
                Vessel shipVessel = evaData.from?.vessel;

                if (evaVessel == null || shipVessel == null)
                {
                    ParsekLog.Warn("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: EVA data missing vessel " +
                        $"(eva={(evaVessel != null ? evaVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}, " +
                        $"ship={(shipVessel != null ? shipVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}) - aborting");
                    yield break;
                }

                uint evaPid = evaVessel.persistentId;
                if (!CheckBranchDeduplication(branchUT, evaPid))
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: skipping duplicate EVA branch for pid={evaPid}");
                    yield break;
                }

                Vessel currentActive = FlightGlobals.ActiveVessel;
                Vessel activeChild;
                Vessel backgroundChild;
                uint currentActivePid = currentActive?.persistentId ?? 0u;
                switch (DecideEvaBackgroundParentFocus(currentActivePid, evaPid, sourceVesselPid))
                {
                    case EvaBackgroundParentFocusDecision.EvaKerbalActive:
                        activeChild = evaVessel;
                        backgroundChild = shipVessel;
                        ParsekLog.Verbose("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: EVA kerbal is active, evaPid={evaPid}");
                        break;

                    case EvaBackgroundParentFocusDecision.SourceVesselActive:
                        activeChild = shipVessel;
                        backgroundChild = evaVessel;
                        ParsekLog.Verbose("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: source vessel is still active, sourcePid={sourceVesselPid}");
                        break;

                    default:
                        ParsekLog.Warn("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: refusing to branch because active vessel is neither " +
                            $"EVA nor source (flightActivePid={currentActivePid}, evaPid={evaPid}, sourcePid={sourceVesselPid})");
                        yield break;
                }

                CreateSplitBranchFromBackgroundParent(
                    parentRecordingId,
                    sourceVesselPid,
                    activeChild,
                    backgroundChild,
                    branchUT,
                    kerbalName,
                    evaPid,
                    branchPath: "background-parent");
            }
            finally
            {
                pendingSplitInProgress = false;
            }
        }

        IEnumerator DeferredJointBreakCheck()
        {
            yield return null; // defer one frame for vessel split to finalize

            try
            {
                if (FlightGlobals.ActiveVessel == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no active vessel after deferral");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                // If there's no pendingSplitRecorder, the recorder was re-established already
                if (pendingSplitRecorder == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no pending split recorder");
                    yield break;
                }

                double currentUT = Planetarium.GetUniversalTime();

                // Scan for NEW vessels created by the joint break.
                // Only consider vessels whose PID was NOT in the pre-break snapshot,
                // to avoid false matches with unrelated vessels (e.g., space stations).
                uint recordedPid = pendingSplitRecorder.RecordingVesselId;

                // Collect new vessel PIDs for classification, tracking per-vessel controller status
                var newVesselPids = new List<uint>();
                var newVesselHasController = new Dictionary<uint, bool>();
                bool anyNewVesselHasController = false;

                // First, include vessels caught by onPartDeCoupleNewVesselComplete during recording.
                // Iterate the PID-keyed decoupleControllerStatus dict rather than the
                // decoupleCreatedVessels List<Vessel>, because at terminal crash time
                // KSP has already destroyed those GameObjects and Unity's overloaded ==
                // makes every element compare equal to null — the old path filtered
                // out every fragment and classified the event as WithinSegment (#362).
                if (decoupleControllerStatus != null && decoupleControllerStatus.Count > 0)
                {
                    ParsekLog.Info("Flight",
                        $"DeferredJointBreakCheck: {decoupleControllerStatus.Count} vessel(s) caught by onPartDeCouple");

                    SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                        recordedPid,
                        decoupleControllerStatus,
                        activeTree?.BackgroundMap,
                        newVesselPids,
                        newVesselHasController,
                        out bool anyNewFromDecouple);

                    if (anyNewFromDecouple)
                        anyNewVesselHasController = true;

                    // Log per-fragment when the live Vessel reference is gone — the fragment
                    // was captured synchronously but terminal destruction already wiped the
                    // GameObject. Classification continues on PID-only data for #362.
                    foreach (var kvp in decoupleControllerStatus)
                    {
                        uint pid = kvp.Key;
                        if (pid == recordedPid) continue;
                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(pid))
                            continue;

                        Vessel live = FlightRecorder.FindVesselByPid(pid);
                        if (live == null)
                        {
                            ParsekLog.Verbose("Flight",
                                $"DeferredJointBreakCheck: pid={pid} captured synchronously but Vessel reference " +
                                $"is destroyed at deferred-check frame — using PID-only classification (#362)");
                        }
                    }
                }

                // Only joint-break-triggered checks have a meaningful pre-break vessel snapshot.
                // For decouple-only fallback, the synchronously-captured decoupleCreatedVessels list
                // is the authoritative source of new vessels. Scanning all live vessels here would
                // misclassify unrelated or stale debris as part of the current split.
                int foreignOriginSkipped = 0;
                if (pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.JointBreak
                    && FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null || v.persistentId == recordedPid) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                        // Only consider vessels that didn't exist before the break
                        if (preBreakVesselPids != null && preBreakVesselPids.Contains(v.persistentId))
                            continue;

                        // Skip vessels we're already tracking in the tree
                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(v.persistentId))
                            continue;

                        // Skip if already captured by onVesselCreate
                        if (newVesselPids.Contains(v.persistentId))
                            continue;

                        // Skip a fragment that came off ANOTHER vessel in this window (e.g. a
                        // spawned copy breaking on its first unpacked frame): it is not a
                        // child of the recording.
                        if (SegmentBoundaryLogic.IsForeignDecoupleOrigin(
                                v.persistentId, recordedPid, decoupleOriginByNewPid))
                        {
                            foreignOriginSkipped++;
                            continue;
                        }

                        if (!ShouldIncludeVesselInDeferredBreakupScan(v, out string rejectReason))
                        {
                            ParsekLog.Verbose("Flight",
                                $"DeferredJointBreakCheck: ignoring unrelated new vessel pid={v.persistentId} " +
                                $"name='{v.vesselName ?? "<unnamed>"}' type={v.vesselType} reason={rejectReason}");
                            continue;
                        }

                        newVesselPids.Add(v.persistentId);

                        bool hasController = ShouldRecordAsOwnBranch(v);
                        newVesselHasController[v.persistentId] = hasController;
                        if (hasController)
                        {
                            anyNewVesselHasController = true;
                        }
                    }
                }
                if (foreignOriginSkipped > 0)
                {
                    ParsekLog.Info("Flight",
                        $"DeferredJointBreakCheck: skipped {foreignOriginSkipped} new vessel(s) " +
                        $"that came off another vessel (decouple origin chain never reaches " +
                        $"recordedPid={recordedPid})");
                }

                string splitCheckSource =
                    pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel
                        ? "decouple-only"
                        : "joint-break";

                // Classify the split result
                var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                    recordedPid, newVesselPids, anyNewVesselHasController);

                double branchUT = ResolveDeferredSplitBranchUT(
                    currentUT,
                    pendingSplitTriggerUT,
                    newVesselPids,
                    decoupleCreatedTrajectoryPoints);

                ParsekLog.Info("Coalescer",
                    $"Deferred split classified: source={splitCheckSource}, result={classification}, " +
                    $"newVessels={newVesselPids.Count}, hasController={anyNewVesselHasController}, " +
                    $"recordedPid={recordedPid}");

                if (classification == JointBreakResult.WithinSegment)
                {
                    // No vessel split occurred — emit segment events and resume recording.
                    // The Decoupled PartEvent was already recorded by FlightRecorder.OnPartJointBreak.
                    // Now emit a SegmentEvent to mark this as within-segment breakage (not a DAG branch).
                    ParsekLog.Info("Coalescer",
                        "Within-segment breakage — emitting SegmentEvents and resuming recording");

                    // Emit SegmentEvent.PartDestroyed for parts that broke off but didn't split
                    // the vessel. Scan recent Decoupled PartEvents to find the broken parts.
                    if (pendingSplitRecorder != null)
                    {
                        var recentDecoupled = pendingSplitRecorder.PartEvents;
                        for (int i = recentDecoupled.Count - 1; i >= 0 && i >= recentDecoupled.Count - 5; i--)
                        {
                            if (recentDecoupled[i].eventType == PartEventType.Decoupled)
                            {
                                SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                                    pendingSplitRecorder.SegmentEvents,
                                    branchUT,
                                    recentDecoupled[i].partName,
                                    recentDecoupled[i].partPersistentId,
                                    false, // wasController — full controller monitoring is Phase 1 low-priority
                                    null);
                            }
                        }
                    }

                    ResumeSplitRecorder(pendingSplitRecorder, "joint break was within-segment (no vessel split)");
                }
                else if (classification == JointBreakResult.StructuralSplit ||
                         classification == JointBreakResult.DebrisSplit)
                {
                    // Vessel physically split — feed ALL new vessels to crash coalescer.
                    // A single joint break can produce multiple new vessels (e.g., 3-way split),
                    // and each needs to be counted individually.
                    ParsekLog.Info("Coalescer",
                        $"Feeding {newVesselPids.Count} new vessel(s) to coalescer: classification={classification}");

                    foreach (var newPid in newVesselPids)
                    {
                        bool hasController = newVesselHasController.ContainsKey(newPid) && newVesselHasController[newPid];

                        // Pre-capture snapshot while the vessel is still alive (#157).
                        // By coalescer emission time (0.5s later), debris may be destroyed.
                        ConfigNode preSnapshot = null;
                        TrajectoryPoint? preTrajectoryPoint = null;
                        string trajectoryPointSource = "none";
                        Vessel childVessel = FlightRecorder.FindVesselByPid(newPid);
                        if (childVessel != null)
                            preSnapshot = FlightRecorder.TryBackupSnapshot(childVessel);
                        TrajectoryPoint capturedPoint = default;
                        bool hasCapturedPoint = decoupleCreatedTrajectoryPoints != null
                            && decoupleCreatedTrajectoryPoints.TryGetValue(newPid, out capturedPoint);
                        if (hasCapturedPoint)
                        {
                            preTrajectoryPoint = capturedPoint;
                            trajectoryPointSource = "decouple-callback";
                        }
                        else if (childVessel != null)
                        {
                            preTrajectoryPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                childVessel, currentUT, preferRootPartSurfacePose: true);
                            trajectoryPointSource = "deferred-live-sample";
                        }
                        else if (childVessel == null)
                        {
                            trajectoryPointSource = "destroyed-before-capture";
                        }

                        // Classify decoupler-initiated (intentional staging / decoupler fire)
                        // vs force-driven structural break. A child caught by
                        // onPartDeCoupleNewVesselComplete (present in decoupleControllerStatus)
                        // came off through Part.decouple(); a decouple-only trigger means the
                        // whole split was detected via that callback. Everything else stays a
                        // breakup so a genuine crash is never relabelled a decouple.
                        bool childWasDecoupleCreated = decoupleControllerStatus != null
                            && decoupleControllerStatus.ContainsKey(newPid);
                        bool triggerWasDecoupleOnly =
                            pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel;
                        uint childRootPartPid = childVessel?.rootPart?.persistentId ?? 0u;
                        string childSplitCause = SegmentBoundaryLogic.ClassifyForegroundSplitChildCause(
                            childWasDecoupleCreated, triggerWasDecoupleOnly,
                            childBrokeUnderForce: pendingSplitRecorder != null
                                && pendingSplitRecorder.JointChildBrokeUnderForce(childRootPartPid),
                            childWasPreAttachedUndock: pendingSplitRecorder != null
                                && pendingSplitRecorder.PartLeftThroughPreAttachedUndock(childRootPartPid),
                            childRootPartPid: childRootPartPid);
                        // Best-available proxy for BranchPoint.DecouplerPartId: the separated
                        // child's root part (the part that came off through the decoupler). KSP's
                        // decouple callback does not hand us the parent-side decoupler module, and
                        // this field has no behavioral consumer (serialized / logged only), so the
                        // child root PID is informative without being load-bearing. 0 when unknown,
                        // matching the background-recorder decouple path.
                        uint childDecouplerPartId = childWasDecoupleCreated
                            ? (childVessel?.rootPart?.persistentId ?? 0u)
                            : 0u;

                        ParsekLog.Info("Coalescer",
                            $"Feeding split to coalescer: childPid={newPid}, childHasController={hasController}" +
                            $", cause={childSplitCause}, decoupleCreated={childWasDecoupleCreated}" +
                            $", triggerDecoupleOnly={triggerWasDecoupleOnly}, decouplerPartId={childDecouplerPartId}" +
                            $", preSnapshot={preSnapshot != null}, preTrajectoryPoint={preTrajectoryPoint.HasValue} " +
                            $"pointSource={trajectoryPointSource} " +
                            $"branchUT={branchUT.ToString("F2", CultureInfo.InvariantCulture)}" +
                            (preTrajectoryPoint.HasValue
                                ? $" seedUT={preTrajectoryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}"
                                : string.Empty));
                        crashCoalescer.OnSplitEvent(
                            branchUT, newPid, hasController,
                            splitCause: childSplitCause,
                            preSnapshot: preSnapshot,
                            preTrajectoryPoint: preTrajectoryPoint,
                            decouplerPartId: childDecouplerPartId);
                        pendingSplitRecorder?.RegisterHighFidelityProximityVessel(
                            newPid,
                            branchUT,
                            "split-child-proximity");
                    }

                    // Stock may already have moved focus to a vessel that came off (the
                    // control part left on it: ModuleDecouple.WaitAndSwitchFocus runs before
                    // this deferred check). The recording still belongs to the vessel it was
                    // recording, so resume on that vessel, not on the active one.
                    Vessel activeAfterSplit = FlightGlobals.ActiveVessel;
                    uint activePidAfterSplit = activeAfterSplit != null ? activeAfterSplit.persistentId : 0u;
                    bool focusLeftRecordedVessel = ShouldResumeSplitOnRecordedVessel(
                        recordedPid,
                        recordedVesselAlive: FlightRecorder.FindVesselByPid(recordedPid) != null,
                        activePidAfterSplit);
                    if (focusLeftRecordedVessel)
                    {
                        ParsekLog.Info("Flight",
                            $"DeferredJointBreakCheck: focus moved off the recorded vessel during the split " +
                            $"(recordedPid={recordedPid}, activePid={activePidAfterSplit}, " +
                            $"activeIsSplitChild={newVesselPids.Contains(activePidAfterSplit)}) - " +
                            $"resuming on the recorded vessel");
                    }

                    // Resume the recorder — the coalescer's Tick will emit the BREAKUP
                    // branch point after the coalescing window expires. The recorder needs
                    // to keep running to capture continued trajectory data.
                    ResumeSplitRecorder(pendingSplitRecorder,
                        $"joint break fed to coalescer (classification={classification})",
                        keepRecordedVessel: focusLeftRecordedVessel);

                    // Bug #263 fallback: ensure a Decoupled event exists for every new
                    // vessel's root part. When a symmetry group of radial decouplers fires,
                    // individual onPartJointBreak events race with KSP's vessel-split
                    // processing and some can be dropped mid-pipeline, leaving decoupled
                    // subtrees visible on the ghost during playback. Scanning new vessel
                    // roots post-split is deterministic — every new debris vessel has
                    // exactly one root part, and that root is the part that separated
                    // from the recording vessel. Must run AFTER ResumeSplitRecorder so
                    // the events survive ResumeAfterFalseAlarm's terminal-event trim.
                    if (recorder != null && recorder.IsRecording)
                    {
                        int fallbackAdded = EmitFallbackDecoupleEventsForNewVessels(
                            recorder, newVesselPids, branchUT);
                        if (fallbackAdded > 0)
                        {
                            ParsekLog.Info("Flight",
                                $"DeferredJointBreakCheck: added {fallbackAdded} fallback " +
                                $"Decoupled event(s) for new vessel roots at " +
                                $"UT={branchUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                        }
                    }

                    // The recorder resumed on a vessel that is no longer the active one: run
                    // the ordinary vessel-switch hand-off now (the stock switch already fired
                    // while the recorder was stopped, so nothing else would). It emits the
                    // pending split first, while this recording is still the active one, so
                    // the new vessel gets its own child recording; then the recorded vessel
                    // goes to the background recorder and the active vessel is handled as a
                    // switch to a tracked background member.
                    if (focusLeftRecordedVessel
                        && recorder != null && recorder.IsRecording
                        && recorder.RecordingVesselId == recordedPid
                        && FlightGlobals.ActiveVessel != null
                        && FlightGlobals.ActiveVessel.persistentId != recordedPid)
                    {
                        ParsekLog.Info("Flight",
                            $"DeferredJointBreakCheck: handing focus over after split " +
                            $"(recordedPid={recordedPid}, activePid={FlightGlobals.ActiveVessel.persistentId})");
                        OnVesselSwitchComplete(FlightGlobals.ActiveVessel);
                    }
                }
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
                pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
                pendingSplitTriggerUT = double.NaN;
                preBreakVesselPids = null;
                // Clear consumed decouple vessels (list stays alive for session, just emptied)
                decoupleCreatedVessels?.Clear();
                decoupleControllerStatus?.Clear();
                decoupleCreatedTrajectoryPoints?.Clear();
                decoupleOriginByNewPid.Clear();
            }
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
                if (newVessel == null && decoupleCreatedVessels != null)
                {
                    for (int j = 0; j < decoupleCreatedVessels.Count; j++)
                    {
                        Vessel dv = decoupleCreatedVessels[j];
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
        void TickCrashCoalescer()
        {
            if (crashCoalescer == null || !crashCoalescer.HasPendingBreakup)
                return;

            double currentUT = Planetarium.GetUniversalTime();
            var breakupBp = crashCoalescer.Tick(currentUT);
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
        void EmitPendingSplitBeforeFocusLeaves(string reason)
        {
            if (crashCoalescer == null || !crashCoalescer.HasPendingBreakup)
                return;
            if (activeTree == null || string.IsNullOrEmpty(activeTree.ActiveRecordingId))
                return;

            double windowStartUT = crashCoalescer.WindowStartUT;
            var breakupBp = crashCoalescer.EmitNow();
            if (breakupBp == null)
                return;

            ParsekLog.Info("Coalescer",
                $"Coalescer emitted split branch point early (focus leaving the active recording, {reason}): " +
                $"ut={breakupBp.UT.ToString("F2", CultureInfo.InvariantCulture)}, type={breakupBp.Type}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"windowAgeSeconds={(Planetarium.GetUniversalTime() - windowStartUT).ToString("F2", CultureInfo.InvariantCulture)}");

            ProcessBreakupEvent(breakupBp);
        }

        /// <summary>
        /// Creates a child recording for a breakup branch point. Handles snapshot capture,
        /// terminal state marking for destroyed vessels, and tree wiring.
        /// Caller is responsible for BackgroundMap registration and logging.
        /// The child inherits Generation = parentGeneration + 1, where parentGeneration
        /// is the breakup parent's recording generation (the active recording or rootRec).
        /// </summary>
        internal static void SeedBreakupChildSnapshots(
            Recording childRec,
            uint pid,
            ConfigNode liveSnapshot,
            ConfigNode preCapturedSnapshot)
        {
            if (childRec == null) return;

            // Prefer the pre-captured split-time snapshot for ghost visuals and start
            // manifests so fast-mutating debris does not spawn from its terminal wreck
            // state on playback. Keep the live snapshot for vessel-spawn state when
            // the child is still alive.
            childRec.GhostVisualSnapshot = preCapturedSnapshot != null
                ? preCapturedSnapshot.CreateCopy()
                : (liveSnapshot != null ? liveSnapshot.CreateCopy() : null);
            childRec.VesselSnapshot = liveSnapshot != null
                ? liveSnapshot.CreateCopy()
                : (preCapturedSnapshot != null ? preCapturedSnapshot.CreateCopy() : null);

            ConfigNode manifestSnapshot = childRec.GhostVisualSnapshot ?? childRec.VesselSnapshot;
            childRec.StartResources = VesselSnapshotOps.ExtractResourceManifest(manifestSnapshot);
            int childInvSlots;
            childRec.StartInventory = VesselSnapshotOps.ExtractInventoryManifest(manifestSnapshot, out childInvSlots);
            childRec.StartInventorySlots = childInvSlots;
            childRec.StartCrew = VesselSpawner.ExtractCrewManifest(manifestSnapshot);

            string snapshotSource = preCapturedSnapshot != null
                ? (liveSnapshot != null ? "pre-captured-ghost + live-vessel" : "pre-captured")
                : (liveSnapshot != null ? "live" : "none");
            ParsekLog.Info("Coalescer",
                $"CreateBreakupChildRecording: pid={pid} snapshotSource={snapshotSource} " +
                $"hasGhostSnapshot={childRec.GhostVisualSnapshot != null} " +
                $"hasVesselSnapshot={childRec.VesselSnapshot != null}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartResources?.Count ?? 0} start resource type(s) for pid={pid}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartInventory?.Count ?? 0} start inventory item type(s) for pid={pid}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartCrew?.Count ?? 0} start crew trait(s) for pid={pid}");
        }

        internal static Recording CreateBreakupChildRecording(
            RecordingTree tree, BranchPoint breakupBp,
            uint pid, Vessel vessel, bool isDebris, string fallbackName,
            ConfigNode fallbackSnapshot = null,
            TrajectoryPoint? fallbackTrajectoryPoint = null,
            int parentGeneration = 0,
            string parentRecordingId = null)
        {
            string childRecId = Guid.NewGuid().ToString("N");
            string vesselName = Recording.ResolveLocalizedName(vessel?.vesselName) ?? fallbackName;

            var childRec = new Recording
            {
                RecordingId = childRecId,
                TreeId = tree.Id,
                VesselPersistentId = pid,
                VesselName = vesselName,
                ParentBranchPointId = breakupBp.Id,
                ExplicitStartUT = breakupBp.UT,
                IsDebris = isDebris,
                Generation = parentGeneration + 1,
                // Pin start-of-recording controller identity from the live breakup-moment
                // vessel. Null when the vessel was destroyed during the coalescing
                // window (no controllable identity to lose anyway — the breakup child
                // already carries TerminalStateValue=Destroyed in that path).
                Controllers = ControllerInfo.CaptureFromVessel(vessel),
                // Launch-unique identity from the same live breakup-moment vessel. Null when
                // it was destroyed during the coalescing window; the snapshot-seeded fallback
                // below covers that case whenever a snapshot exists at all.
                RecordedVesselGuid = AnchorDetector.TryReadLiveVesselGuid(vessel)
            };
            // Stamp the parent-anchor contract on the new child Recording. The breakup
            // branch point's ParentRecordingIds list can have multiple parents in
            // chain-merge cases, so passing parentRecordingId explicitly avoids
            // ambiguity; the focused recording is the right anchor. Caller-decides
            // helper: both debris (IsDebris=true) and controlled-decoupled children
            // (IsDebris=false) receive the contract here.
            Recording.ApplyParentAnchorContract(childRec, parentRecordingId);
            if (!string.IsNullOrEmpty(childRec.ParentAnchorRecordingId))
            {
                string population = childRec.IsDebris ? "debris" : "controlled-child";
                ParsekLog.Verbose("Coalescer",
                    $"Parent-anchor contract applied at breakup: childRecId={childRecId} " +
                    $"childPid={pid} population={population} " +
                    $"parentRecId={parentRecordingId ?? "(none)"}");
            }

            // Bug #419 — invariant-enforcement: before any seed-point or snapshot code
            // runs, trim any pre-existing trajectory points at or after the breakup UT.
            // Debris recordings start empty in the normal path (CreateBreakupChildRecording
            // allocates a fresh Recording via `new Recording { ... }`), so this is a no-op
            // under current callers; but if any future inheritance path grafts parent
            // samples into the child Recording before this function runs, those samples
            // would stitch onto the post-boundary sampler stream and produce the
            // Points[n].ut < Points[n-1].ut failure signature that
            // `CommittedRecordingsHaveValidData` flags. Inclusive trim at breakupBp.UT
            // guarantees the authoritative seed at breakupBp.UT (either fallbackTrajectoryPoint
            // below, or OnVesselBackgrounded's pendingInitialTrajectoryPoint) becomes
            // Points[0] cleanly. The fallbackTrajectoryPoint path supplies its own
            // post-boundary seed (captured during the coalescing window, UT > breakupBp.UT
            // in the vessel-destroyed-during-window scenario) and is applied AFTER the
            // trim, so it is never mistakenly removed.
            // Defensive: under current callers childRec.Points is empty when we reach this line,
            // so the trim is a no-op and the warn below will not fire. Kept to lock the invariant
            // against future inheritance paths that might pre-seed post-boundary samples.
            int trimmedAtBoundary = FlightRecorder.TrimPointsAtOrAfterUT(childRec.Points, breakupBp.UT);
            if (trimmedAtBoundary > 0)
            {
                ParsekLog.Warn("Coalescer",
                    $"CreateBreakupChildRecording: trimmed {trimmedAtBoundary} inherited point(s) at/after " +
                    $"breakup UT={breakupBp.UT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"from child recId={childRec.RecordingId} (pid={pid}) to preserve monotonicity (#419)");
            }

            TrajectoryPoint? structuralSeedPoint = ApplyStructuralEventFlagToChildSeed(
                fallbackTrajectoryPoint,
                breakupBp.UT);

            if (vessel != null)
            {
                ConfigNode liveSnapshot = FlightRecorder.TryBackupSnapshot(vessel);
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot, fallbackSnapshot);
            }
            else if (fallbackSnapshot != null)
            {
                // Vessel destroyed during coalescing window — use pre-captured snapshot (#157)
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot: null, preCapturedSnapshot: fallbackSnapshot);
                childRec.StampTerminalState(
                    TerminalState.Destroyed, "CreateBreakupChildRecording.preCapturedSnapshot");
                childRec.ExplicitEndUT = breakupBp.UT;
                if (structuralSeedPoint.HasValue)
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, structuralSeedPoint.Value);
                ParsekLog.Info("Coalescer",
                    $"CreateBreakupChildRecording: using pre-captured snapshot for pid={pid} (vessel destroyed)");
            }
            else
            {
                childRec.StampTerminalState(
                    TerminalState.Destroyed, "CreateBreakupChildRecording.noSnapshot");
                childRec.ExplicitEndUT = breakupBp.UT;
                if (structuralSeedPoint.HasValue)
                {
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, structuralSeedPoint.Value);
                    ParsekLog.Info("Coalescer",
                        $"CreateBreakupChildRecording: using pre-captured trajectory point for pid={pid} (vessel destroyed)");
                }
            }

            // Destroyed-vessel arms: the live read above returned nothing, so take the guid
            // from whichever snapshot was seeded (its top-level `pid` IS the Vessel.id) -
            // the same value the load-time backfill reads, applied before the first save.
            // The no-vessel-AND-no-snapshot arm keeps no launch identity at all; there is
            // nothing left to read it from, and that child is stamped Destroyed at the
            // breakup UT with no trajectory of its own.
            if (childRec.AdoptRecordedVesselGuidIfEmpty(
                    VesselLaunchIdentity.TryReadVesselGuid(childRec.VesselSnapshot)
                    ?? VesselLaunchIdentity.TryReadVesselGuid(childRec.GhostVisualSnapshot)))
            {
                ParsekLog.Verbose("Coalescer",
                    $"CreateBreakupChildRecording: launch guid taken from the seeded snapshot " +
                    $"for pid={pid} recId={childRecId} guid={childRec.RecordedVesselGuid}");
            }

            tree.AddOrReplaceRecording(childRec);
            breakupBp.ChildRecordingIds.Add(childRecId);

            return childRec;
        }

        private static string DescribeBreakupChildSeedPose(
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
                $"vel={FormatVector3(point.velocity)}";

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
                $"seed-liveRoot={FormatVector3d(seedRootDelta)} " +
                $"seedLiveRootDist={seedRootDelta.magnitude.ToString("F2", ic)}m";
        }

        private static TrajectoryPoint? ResolveControlledChildInitialTrajectoryPoint(
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
            bool preferLive = ShouldPreferLiveBreakupChildSeed(
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
                    $"threshold={ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m");
                return capturedPoint;
            }

            bool directSeedMissesLiveRoot =
                IsFinite(seedLiveRootDistance)
                && seedLiveRootDistance > ControlledChildLiveSeedResidualToleranceMeters;
            string replacementReason = directSeedMissesLiveRoot
                ? "seed-misses-live-root"
                : "propagated-seed-misses-live-root";
            ParsekLog.Info("Coalescer",
                $"Controlled child initial seed replaced with live sample: pid={pid} " +
                $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"seedLiveRootDist={seedLiveRootDistance.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"propagatedResidual={propagatedSeedResidual.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"threshold={ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"source=live-root-part reason={replacementReason}");
            return livePoint;
        }

        private static bool TryComputeBreakupSeedLiveRootDistance(
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
            propagatedResidualMeters = ComputeBreakupSeedPropagatedResidualMeters(
                seedWorld,
                seedPoint.velocity,
                seedPoint.ut,
                rootWorld,
                liveUT);
            return IsFinite(distanceMeters) && IsFinite(propagatedResidualMeters);
        }

        private static string FormatVector3(Vector3 value)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"({value.x.ToString("F2", ic)}," +
                   $"{value.y.ToString("F2", ic)}," +
                   $"{value.z.ToString("F2", ic)})";
        }

        internal static string FormatVector3d(Vector3d value)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"({value.x.ToString("F2", ic)}," +
                   $"{value.y.ToString("F2", ic)}," +
                   $"{value.z.ToString("F2", ic)})";
        }

        /// <summary>
        /// True when a background parent must not be split again: it carries a child branch
        /// point that already continued its vessel (a same-PID child, or a merge). A
        /// breakup-continuous parent (a decouple or breakup whose children are only the stages
        /// and debris it dropped) is still its vessel's line and may be split, as the
        /// foreground <c>CreateSplitBranch</c> already does.
        /// </summary>
        internal static bool BackgroundParentAlreadySplit(Recording parentRecording, RecordingTree tree)
        {
            if (parentRecording == null || string.IsNullOrEmpty(parentRecording.ChildBranchPointId))
                return false;
            return tree == null
                || !RecordingTree.ChildBranchLeavesVesselLineOpen(
                    parentRecording, tree.Recordings, tree.BranchPoints);
        }

        /// <summary>
        /// Wires a coalescer-emitted BREAKUP branch point into the tree: the active
        /// recording becomes its parent, the tree gains the branch point, and the
        /// recording's ChildBranchPointId links to it.
        /// </summary>
        internal static void WireBreakupIntoTree(
            RecordingTree tree, string activeRecId, Recording activeRec, BranchPoint breakupBp)
        {
            breakupBp.ParentRecordingIds.Add(activeRecId);
            tree.BranchPoints.Add(breakupBp);
            activeRec.ChildBranchPointId = breakupBp.Id;
        }

        /// <summary>
        /// Processes a BREAKUP branch point emitted by the crash coalescer.
        /// Adds the BREAKUP event as a branch point on the active tree recording and
        /// creates child recordings for debris and controlled vessels.
        /// If no active tree exists, the breakup is not recorded.
        /// </summary>
        void ProcessBreakupEvent(BranchPoint breakupBp)
        {
            if (activeTree == null)
            {
                ParsekLog.Info("Coalescer",
                    "ProcessBreakupEvent: no active tree — breakup not recorded. " +
                    $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}");
                return;
            }

            string activeRecId = activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(activeRecId))
            {
                ParsekLog.Warn("Coalescer",
                    "ProcessBreakupEvent: active tree has no active recording — cannot attach breakup event");
                return;
            }

            Recording activeRec;
            if (!activeTree.Recordings.TryGetValue(activeRecId, out activeRec))
            {
                ParsekLog.Warn("Coalescer",
                    $"ProcessBreakupEvent: active recording id={activeRecId} not found in tree");
                return;
            }

            WireBreakupIntoTree(activeTree, activeRecId, activeRec, breakupBp);

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
            uint parentVesselPid = ResolveBreakupParentVesselPid(
                recorder != null ? recorder.RecordingVesselId : 0u,
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
            InheritedEngineState? breakupEngineState = recorder != null
                ? InheritedEngineState.FromRecorder(recorder) : null;

            TrajectoryPoint? focusedParentSeedAnchorPoint = null;
            if (recorder != null
                && !recorder.CurrentTrackSectionUsesRelativeFrame
                && FlightRecorder.TryFindStructuralEventSnapshotPointForUT(
                    recorder?.Recording,
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
                    $"recorderPoints={recorder?.Recording?.Count ?? 0} " +
                    $"currentSectionRelative={recorder?.CurrentTrackSectionUsesRelativeFrame ?? false}");
            }

            // Create child recording segments for controlled children (vessels with probe
            // cores that survive the breakup).
            // These are NOT debris — they record indefinitely (no TTL) and can serve as
            // RELATIVE anchors during playback.
            var controlledChildren = crashCoalescer.LastEmittedControlledChildPids;

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
                    ConfigNode ctrlSnap = crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ApplyStructuralEventFlagToChildSeed(
                        crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);

                    // Skip dead-on-arrival controlled children: once the live
                    // Vessel is gone before the coalescer window expires there
                    // is no controlled continuation to sample. A pre-captured
                    // snapshot only turns that into a destroyed one-point
                    // "Unknown" row, so leave the split represented by the
                    // parent BREAKUP branch point.
                    if (ShouldSkipDeadOnArrivalControlledChild(
                            childVesselIsAlive: childVessel != null,
                            hasPreCapturedSnapshot: ctrlSnap != null))
                    {
                        ParsekLog.Info("Coalescer",
                            BuildDeadOnArrivalControlledChildSkipLog(
                                pid,
                                hasPreCapturedSnapshot: ctrlSnap != null));
                        continue;
                    }

                    TrajectoryPoint? initialPoint = ResolveControlledChildInitialTrajectoryPoint(
                        pid,
                        breakupChildPoint,
                        childVessel,
                        Planetarium.GetUniversalTime());
                    initialPoint = ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);

                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, childVessel, false, "Unknown",
                        ctrlSnap, initialPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL - records indefinitely)
                    if (childVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        // Parent-anchored controlled-decoupled children carry the same
                        // structural-event parent-anchor seed as debris siblings so the
                        // first BG sample after split lands on the deterministic split-
                        // moment parent pose rather than relying on a post-split live
                        // resolve. The debris loop below queues the same seed at line 6470.
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                    }

                    if (childVessel != null && childRec != null)
                        breakupControllablePairs.Add((childVessel, childRec));

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: controlled child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={childVessel != null}, bgRecorder={backgroundRecorder != null} " +
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
            var debrisPids = crashCoalescer.LastEmittedDebrisPids;
            if (debrisPids != null && debrisPids.Count > 0)
            {
                double debrisExpiryUT = breakupBp.UT + BackgroundRecorder.DebrisTTLSeconds;
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    uint pid = debrisPids[i];
                    Vessel debrisVessel = FlightRecorder.FindVesselByPid(pid);

                    if (debrisVessel != null && !ShouldRecordDebris(debrisVessel))
                    {
                        skippedDebris++;
                        continue;
                    }

                    // Use the coalescer's split-time snapshot for ghost visuals even when
                    // the debris vessel survives the coalescing window; VesselSnapshot still
                    // comes from the later live state for spawning.
                    ConfigNode preSnap = crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ApplyStructuralEventFlagToChildSeed(
                        crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);
                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, debrisVessel, true, "Debris",
                        preSnap, breakupChildPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    if (debrisVessel != null && backgroundRecorder != null)
                    {
                        TrajectoryPoint? initialPoint = breakupChildPoint;
                        if (!initialPoint.HasValue)
                        {
                            double sampleUT = Planetarium.GetUniversalTime();
                            initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                debrisVessel, sampleUT, preferRootPartSurfacePose: true);
                        }
                        initialPoint = ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                        backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: debris child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={debrisVessel != null} " +
                        $"{DescribeBreakupChildSeedPose(breakupChildPoint, debrisVessel, Planetarium.GetUniversalTime())}");
                }
            }

            ParsekLog.Info("Coalescer",
                $"ProcessBreakupEvent: {breakupBp.Type} attached to tree={activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"skippedTrivial={skippedDebris}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");
        }
    }
}
