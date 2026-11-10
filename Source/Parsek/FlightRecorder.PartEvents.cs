using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Part event subscriptions, structural part events (die / joint break / decouple), visual
    // coverage diagnostics, robotic part state and module field readers, and part-state tracking
    // reset and seeding.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Consumes the pending joint break flag, returning whether a check was pending.
        /// </summary>
        public bool ConsumePendingJointBreakCheck(out double jointBreakUT)
        {
            bool was = HasPendingJointBreakCheck;
            jointBreakUT = PendingJointBreakUT;
            HasPendingJointBreakCheck = false;
            PendingJointBreakUT = double.NaN;
            return was;
        }

        internal bool TryConsumePendingJointChildPartOriginSeed(uint partPersistentId, out TrajectoryPoint point)
        {
            point = default;
            if (partPersistentId == 0u
                || !pendingJointChildPartOriginSeeds.TryGetValue(partPersistentId, out point))
            {
                return false;
            }

            pendingJointChildPartOriginSeeds.Remove(partPersistentId);
            return true;
        }

        internal string DescribePendingJointChildPartOriginSeedIdsForDiagnostics(int maxIds = 8)
        {
            return FormatPendingJointChildPartOriginSeedIdsForDiagnostics(
                pendingJointChildPartOriginSeeds.Keys,
                maxIds);
        }

        internal bool ContainsPendingJointChildPartOriginSeed(uint partPersistentId)
        {
            return PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(
                pendingJointChildPartOriginSeeds.Keys,
                partPersistentId);
        }

        internal static string FormatPendingJointChildPartOriginSeedIdsForDiagnostics(
            IEnumerable<uint> partPersistentIds,
            int maxIds = 8)
        {
            if (partPersistentIds == null)
                return "none";

            int limit = Math.Max(1, maxIds);
            var ids = new List<string>();
            bool truncated = false;
            foreach (uint pid in partPersistentIds)
            {
                if (ids.Count < limit)
                {
                    ids.Add(pid.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                truncated = true;
                break;
            }

            if (ids.Count == 0)
                return "none";

            string suffix = truncated ? ",..." : string.Empty;
            return string.Join(",", ids.ToArray()) + suffix;
        }

        internal static bool PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(
            IEnumerable<uint> partPersistentIds,
            uint partPersistentId)
        {
            if (partPersistentId == 0u || partPersistentIds == null)
                return false;

            foreach (uint pid in partPersistentIds)
            {
                if (pid == partPersistentId)
                    return true;
            }

            return false;
        }

        #region Part Event Subscription

        private void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnPartJointBreak);
            GameEvents.onPartDeCouple.Add(OnPartDeCouple);
            partEventsSubscribed = true;
            ParsekLog.Verbose("Recorder", "Subscribed part event hooks");
        }

        private void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onPartJointBreak.Remove(OnPartJointBreak);
            GameEvents.onPartDeCouple.Remove(OnPartDeCouple);
            partEventsSubscribed = false;
            ParsekLog.Verbose("Recorder", "Unsubscribed part event hooks");
        }

        private void OnPartDie(Part p)
        {
            if (!IsRecording) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "part-die-null",
                    "OnPartDie: part or vessel is null");
                return;
            }
            if (p.vessel.persistentId != RecordingVesselId) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = ClassifyPartDeath(p.persistentId, hasChute, parachuteStates);

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            ActivateHighFidelitySampling(PartEvents[PartEvents.Count - 1].ut, $"part-event-{evtType}");
            ParsekLog.Verbose("Recorder", $"Part event: {evtType} '{p.partInfo?.name}' pid={p.persistentId}");
            RefreshFinalizationCache(p.vessel, "part_die", force: true);
        }

        internal static bool TryCreateAbsoluteTrajectoryPointFromPartOrigin(
            Part part,
            double ut,
            out TrajectoryPoint point)
        {
            point = default;
            Vessel vessel = part?.vessel;
            CelestialBody body = vessel?.mainBody;
            if (part?.transform == null || body?.bodyTransform == null)
                return false;

            // Part-origin seeds are used for physics-frame separation events. Packed
            // vessels expose orbital velocity in a different frame contract, so fail
            // closed rather than mixing inertial velocity into a body-fixed seed.
            if (vessel.packed)
                return false;

            Vector3 velocity = (Vector3)(vessel.rb_velocityD + Krakensbane.GetFrameVelocity());
            Vector3d worldPos = part.transform.position;

            point = new TrajectoryPoint
            {
                ut = ut,
                latitude = body.GetLatitude(worldPos),
                longitude = body.GetLongitude(worldPos),
                altitude = body.GetAltitude(worldPos),
                // Consumers accept this seed only when this part becomes the new
                // vessel root, so the part's surface-relative rotation is the
                // child vessel's initial srfRelRotation contract.
                rotation = Quaternion.Inverse(body.bodyTransform.rotation) * part.transform.rotation,
                velocity = velocity,
                bodyName = body.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                recordedGroundClearance = double.NaN
            };
            // ---- Trace-Sep ----
            if (TraceSeparation.RecordingWindowActive)
            {
                // Parent-vessel reference position derived from the stale-LLA
                // path. At joint-break callback time VesselPrecalculate has
                // already run pre-PhysX, so v.latitude/longitude/altitude
                // reflect start-of-tick state while part.transform.position
                // reflects end-of-tick PhysX state. The delta is the candidate
                // offset that the reverted fix tried to back-step out of the
                // seed; logging both the delta and what predicted-back-step
                // magnitudes look like at this vessel's velocities lets the
                // log reader confirm direction and frame before re-attempting
                // any correction.
                Vector3d vesselLlaWorld = body.GetWorldSurfacePosition(
                    vessel.latitude, vessel.longitude, vessel.altitude);
                Vector3 srfVel = vessel.srf_velocity;
                float dt = Time.fixedDeltaTime;
                double predictedSrfStep = srfVel.magnitude * dt;
                double predictedInertialStep = velocity.magnitude * dt;
                double observedDelta = (worldPos - vesselLlaWorld).magnitude;
                TraceSeparation.RecordLog("PartOriginSeed",
                    "vesselPid=" + vessel.persistentId +
                    " partPid=" + part.persistentId +
                    " partName='" + (part.partInfo?.name ?? part.name) + "'" +
                    " ut=" + ut.ToString("R", CultureInfo.InvariantCulture) +
                    " partTransformPos=" + TraceSeparation.FormatVector3d(worldPos) +
                    " resultLLA=(" + point.latitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + point.longitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + point.altitude.ToString("R", CultureInfo.InvariantCulture) + ")" +
                    " velIn=" + TraceSeparation.FormatVector3(velocity) + " |v|=" + velocity.magnitude.ToString("R", CultureInfo.InvariantCulture) +
                    " srfVel=" + TraceSeparation.FormatVector3(srfVel) + " |sv|=" + srfVel.magnitude.ToString("R", CultureInfo.InvariantCulture) +
                    " vesselTransformPos=" + (vessel.transform != null ? TraceSeparation.FormatVector3d(vessel.transform.position) : "<null>") +
                    " vesselLLA=(" + vessel.latitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + vessel.longitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + vessel.altitude.ToString("R", CultureInfo.InvariantCulture) + ")" +
                    " vesselLLAWorld=" + TraceSeparation.FormatVector3d(vesselLlaWorld) +
                    " partVsVesselLLA=" + TraceSeparation.FormatVector3d(worldPos - vesselLlaWorld) +
                    " |observedDelta|=" + observedDelta.ToString("F3", CultureInfo.InvariantCulture) +
                    " predictedSrfStep=" + predictedSrfStep.ToString("F3", CultureInfo.InvariantCulture) +
                    " predictedInertialStep=" + predictedInertialStep.ToString("F3", CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----
            return true;
        }

        private static string DescribeJointPartForDiagnostics(string label, Part part)
        {
            if (part == null)
                return label + "=null";

            string partName = part.partInfo?.name ?? part.name ?? "unknown";
            uint vesselPid = part.vessel?.persistentId ?? 0u;
            string vesselName = part.vessel?.vesselName ?? "unknown";
            uint parentPid = part.parent?.persistentId ?? 0u;
            string origin = part.transform != null
                ? DiagnosticFormatters.FormatVector3d(part.transform.position)
                : "no-transform";
            string rotation = part.transform != null
                ? DiagnosticFormatters.FormatQuaternion(part.transform.rotation)
                : "no-transform";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}='{1}' pid={2} parentPid={3} vesselPid={4} vessel='{5}' origin={6} rot={7} srfAttach={8}",
                label,
                partName,
                part.persistentId,
                parentPid,
                vesselPid,
                vesselName,
                origin,
                rotation,
                DiagnosticFormatters.DescribeSurfaceAttachNode(part));
        }

        private void OnPartJointBreak(PartJoint joint, float breakForce)
        {
            if (!IsRecording) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "joint-break-null",
                    "OnPartJointBreak: joint, child, or vessel is null");
                return;
            }
            if (joint.Child.vessel.persistentId != RecordingVesselId) return;

            // ---- Trace-Sep: open recording window + log break state ----
            if (ParsekSettings.Current?.ghostRenderTracing == true)
                TraceJointBreakSeparation(joint, breakForce);
            // ---- /Trace-Sep ----

            // Skip non-structural joint breaks (e.g., wheel suspension stress)
            // Part.attachJoint is the structural connection to parent — only that indicates real separation
            var childAttachJoint = joint.Child.attachJoint;
            bool childAttachMatchesJoint = joint == childAttachJoint;
            bool childAttachJointPresent = childAttachJoint != null;
            bool structuralJointBreak = IsStructuralJointBreak(
                childAttachMatchesJoint,
                childAttachJointPresent);
            double jointBreakUT = Planetarium.GetUniversalTime();
            LogJointBreakDiagnostics(
                joint,
                breakForce,
                jointBreakUT,
                structuralJointBreak,
                childAttachMatchesJoint,
                childAttachJointPresent);
            if (!structuralJointBreak)
            {
                ParsekLog.VerboseRateLimited("Recorder", $"joint-break-nonstructural-{joint.Child.persistentId}",
                    $"OnPartJointBreak: non-structural joint break on '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"childAttachMatchesJoint={(childAttachMatchesJoint ? "T" : "F")} " +
                    $"childAttachJointPresent={(childAttachJointPresent ? "T" : "F")} " +
                    $"breakForce={breakForce.ToString("F1", CultureInfo.InvariantCulture)}, skipping");
                return;
            }

            // Skip duplicate decoupled events for the same part
            if (decoupledPartIds.Contains(joint.Child.persistentId))
            {
                ParsekLog.VerboseRateLimited("Recorder", $"joint-break-dup-{joint.Child.persistentId}",
                    $"OnPartJointBreak: duplicate for already-decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}, skipping");
                return;
            }
            MarkJointChildDecoupled(joint, breakForce);

            CaptureJointChildPartOriginSeed(joint, jointBreakUT);

            RecordJointBreakDecoupledEvent(joint, jointBreakUT);
            RefreshFinalizationCache(joint.Child.vessel, "joint_break", force: true);

            AppendJointBreakStructuralSnapshot(joint, jointBreakUT);

            NotePendingJointBreakCheck(jointBreakUT);
        }

        /// <summary>
        /// The Trace-Sep break-state lines (gated by ghostRenderTracing at the call site):
        /// opens the recording window and logs the parent vessel and the child part at the
        /// joint-break instant. Every read is a live Unity / KSP read.
        /// </summary>
        private static void TraceJointBreakSeparation(PartJoint joint, float breakForce)
        {
            Vessel parentVessel = joint.Child.vessel;
            double evUT = Planetarium.GetUniversalTime();
            TraceSeparation.OpenRecordingWindow(evUT,
                "OnPartJointBreak childPid=" + joint.Child.persistentId);
            if (parentVessel != null && parentVessel.mainBody != null)
            {
                CelestialBody body = parentVessel.mainBody;
                Vector3d livePos = body.GetWorldSurfacePosition(
                    parentVessel.latitude, parentVessel.longitude, parentVessel.altitude);
                Vector3 inertialVel = parentVessel.packed
                    ? (Vector3)parentVessel.obt_velocity
                    : (Vector3)(parentVessel.rb_velocityD + Krakensbane.GetFrameVelocity());
                Vector3 surfaceVel = parentVessel.srf_velocity;
                Vector3d transformPos = parentVessel.transform != null
                    ? parentVessel.transform.position : Vector3d.zero;
                // Predicted one-tick back-step magnitudes for the two
                // candidate velocity frames. If the reverted fix's
                // inertial-velocity hypothesis is right, |predictedInertialStep|
                // should match |worldDeltaTransformVsLLA| at the joint-break
                // instant. If body-rotation contamination is the culprit
                // (per the critical-analysis path) |predictedSrfStep| matches
                // and inertial overshoots by `omega_body x position * dt`
                // (~3.5 m at Kerbin equator). Logging both lets the log
                // reader pick the winning frame without rerunning.
                float dt = Time.fixedDeltaTime;
                double predictedSrfStep = surfaceVel.magnitude * dt;
                double predictedInertialStep = inertialVel.magnitude * dt;
                double observedDelta = (transformPos - livePos).magnitude;
                TraceSeparation.RecordLog("JointBreak",
                    "PARENT_AT_BREAK pid=" + parentVessel.persistentId +
                    " name='" + parentVessel.vesselName + "' breakForce=" + breakForce.ToString("F2", CultureInfo.InvariantCulture) +
                    " latLonAlt=(" + parentVessel.latitude.ToString("R", CultureInfo.InvariantCulture) +
                    ", " + parentVessel.longitude.ToString("R", CultureInfo.InvariantCulture) +
                    ", " + parentVessel.altitude.ToString("R", CultureInfo.InvariantCulture) + ")" +
                    " livePos=" + TraceSeparation.FormatVector3d(livePos) +
                    " transformPos=" + TraceSeparation.FormatVector3d(transformPos) +
                    " worldDeltaTransformVsLLA=" + TraceSeparation.FormatVector3d(transformPos - livePos) +
                    " |observedDelta|=" + observedDelta.ToString("F3", CultureInfo.InvariantCulture) +
                    " inertialVel=" + TraceSeparation.FormatVector3(inertialVel) + " |v|=" + inertialVel.magnitude.ToString("F3", CultureInfo.InvariantCulture) +
                    " surfaceVel=" + TraceSeparation.FormatVector3(surfaceVel) + " |vs|=" + surfaceVel.magnitude.ToString("F3", CultureInfo.InvariantCulture) +
                    " predictedSrfStep=" + predictedSrfStep.ToString("F3", CultureInfo.InvariantCulture) +
                    " predictedInertialStep=" + predictedInertialStep.ToString("F3", CultureInfo.InvariantCulture) +
                    " packed=" + parentVessel.packed);
            }
            if (joint.Child != null && joint.Child.transform != null)
            {
                // Compare the child part's PhysX transform.position
                // (end-of-tick state) to the parent vessel's
                // body.GetWorldSurfacePosition(LLA) (stale pre-PhysX LLA
                // updated by VesselPrecalculate.CalculatePhysicsStats in
                // this same FixedUpdate). The signed delta in the parent's
                // velocity direction is the smoking-gun debris-side offset:
                // if it is positive and matches |srfVel|*dt at the parent's
                // surface velocity, the joint-child part-origin seed needs
                // a back-step; if it is near zero, the seed is in-phase.
                Vector3d childPos = joint.Child.transform.position;
                Vector3d childVsParentLla = parentVessel != null && parentVessel.mainBody != null
                    ? (childPos - parentVessel.mainBody.GetWorldSurfacePosition(
                        parentVessel.latitude, parentVessel.longitude, parentVessel.altitude))
                    : Vector3d.zero;
                Vector3 parentSrfVel = parentVessel != null ? parentVessel.srf_velocity : Vector3.zero;
                double childVsParentAlongVel = parentSrfVel.sqrMagnitude > 0f
                    ? Vector3d.Dot(childVsParentLla, (Vector3d)parentSrfVel.normalized)
                    : 0.0;
                TraceSeparation.RecordLog("JointBreak",
                    "CHILD_PART_AT_BREAK pid=" + joint.Child.persistentId +
                    " name='" + (joint.Child.partInfo?.name ?? joint.Child.name) + "'" +
                    " transformPos=" + TraceSeparation.FormatVector3d(childPos) +
                    " childVsParentLLA=" + TraceSeparation.FormatVector3d(childVsParentLla) +
                    " |delta|=" + childVsParentLla.magnitude.ToString("F3", CultureInfo.InvariantCulture) +
                    " alongParentSrfVel=" + childVsParentAlongVel.ToString("F3", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// The rate-limited per-child diagnostics line for a joint break, structural or not.
        /// </summary>
        private static void LogJointBreakDiagnostics(
            PartJoint joint,
            float breakForce,
            double jointBreakUT,
            bool structuralJointBreak,
            bool childAttachMatchesJoint,
            bool childAttachJointPresent)
        {
        ParsekLog.VerboseRateLimited("Recorder",
            $"joint-break-diag-{joint.Child.persistentId}",
            $"OnPartJointBreak diagnostics: ut={jointBreakUT.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"breakForce={breakForce.ToString("F1", CultureInfo.InvariantCulture)} " +
            $"structural={(structuralJointBreak ? "T" : "F")} " +
            $"childAttachMatchesJoint={(childAttachMatchesJoint ? "T" : "F")} " +
            $"childAttachJointPresent={(childAttachJointPresent ? "T" : "F")} " +
            $"{DescribeJointPartForDiagnostics("child", joint.Child)} " +
            $"{DescribeJointPartForDiagnostics("parent", joint.Parent)} " +
            $"{DescribeJointPartForDiagnostics("host", joint.Host)} " +
            $"{DescribeJointPartForDiagnostics("target", joint.Target)} " +
            $"hostAnchor={DiagnosticFormatters.FormatVector3(joint.HostAnchor)} " +
            $"targetAnchor={DiagnosticFormatters.FormatVector3(joint.TgtAnchor)}",
            2.0);
        }

        /// <summary>
        /// Marks the joint child as decoupled, and as broken off under force when the joint
        /// broke with a positive force.
        /// </summary>
        internal void MarkJointChildDecoupled(PartJoint joint, float breakForce)
        {
        decoupledPartIds.Add(joint.Child.persistentId);
        if (breakForce > 0f)
        {
            forceBrokenJointChildPartIds.Add(joint.Child.persistentId);
            ParsekLog.Verbose("Recorder",
                $"OnPartJointBreak: joint broke under force pid={joint.Child.persistentId} " +
                $"breakForce={breakForce.ToString("F1", CultureInfo.InvariantCulture)} => split cause BROKE_OFF");
        }
        }

        /// <summary>
        /// Captures the joint child's part-origin seed at the break UT, or logs (rate-limited)
        /// why none is available. The seed read is a live part-transform read.
        /// </summary>
        private void CaptureJointChildPartOriginSeed(PartJoint joint, double jointBreakUT)
        {
        if (TryCreateAbsoluteTrajectoryPointFromPartOrigin(
                joint.Child,
                jointBreakUT,
                out TrajectoryPoint childPartOriginSeed))
        {
            pendingJointChildPartOriginSeeds[joint.Child.persistentId] = childPartOriginSeed;
            ParsekLog.Verbose("Recorder",
                $"Joint child part-origin seed captured: part='{joint.Child.partInfo?.name}' " +
                $"pid={joint.Child.persistentId} ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        else
        {
            ParsekLog.VerboseRateLimited("Recorder",
                $"joint-break-part-origin-seed-miss-{joint.Child.persistentId}",
                $"Joint child part-origin seed unavailable: part='{joint.Child.partInfo?.name}' " +
                $"pid={joint.Child.persistentId} vesselPacked={(joint.Child.vessel?.packed == true ? "T" : "F")} " +
                $"ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        }

        /// <summary>
        /// Records the Decoupled part event for the joint child and opens the high-fidelity
        /// sampling window at the break UT.
        /// </summary>
        internal void RecordJointBreakDecoupledEvent(PartJoint joint, double jointBreakUT)
        {
        PartEvents.Add(new PartEvent
        {
            ut = jointBreakUT,
            partPersistentId = joint.Child.persistentId,
            eventType = PartEventType.Decoupled,
            partName = joint.Child.partInfo?.name ?? "unknown"
        });
        ActivateHighFidelitySampling(jointBreakUT, "joint-break");
        ParsekLog.Verbose("Recorder", $"Part event: Decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}");
        }

        /// <summary>
        /// Appends the structural-event snapshot for the vessels a joint break involves.
        /// </summary>
        internal void AppendJointBreakStructuralSnapshot(PartJoint joint, double jointBreakUT)
        {
        // Phase 9 (design doc §12, §18 Phase 9): synthesize a structural-event
        // snapshot for the recorded vessel at the exact joint-break UT so
        // AnchorCandidateBuilder gets a physics-precision sample for the
        // resulting BranchPointType.JointBreak ε. Both the joint host and the
        // (pre-split) child still share v.persistentId == RecordingVesselId
        // here; only the recording vessel matches and gets a snapshot.
        // joint.Host.vessel and joint.Child.vessel typically reference the
        // same Vessel pre-split, so we pass both — AppendStructuralEventSnapshot
        // dedups by RecordingVesselId.
        var jointBreakInvolved = new List<Vessel>(2);
        if (joint.Child.vessel != null) jointBreakInvolved.Add(joint.Child.vessel);
        if (joint.Host?.vessel != null && (joint.Host.vessel != joint.Child.vessel))
            jointBreakInvolved.Add(joint.Host.vessel);
        AppendStructuralEventSnapshot(jointBreakUT, jointBreakInvolved, "JointBreak");
        }

        /// <summary>
        /// Arms (or moves earlier) the deferred vessel-split check ParsekFlight runs after a
        /// joint break.
        /// </summary>
        internal void NotePendingJointBreakCheck(double jointBreakUT)
        {
        // Signal potential vessel split for deferred check by ParsekFlight
        if (!HasPendingJointBreakCheck || double.IsNaN(PendingJointBreakUT) || jointBreakUT < PendingJointBreakUT)
            PendingJointBreakUT = jointBreakUT;
        HasPendingJointBreakCheck = true;
        }

        /// <summary>
        /// Records a fallback Decoupled PartEvent for a part that split off into a new vessel.
        /// Called by <see cref="ParsekFlight.DeferredJointBreakCheck"/> after the vessel-split
        /// classification, once per new vessel's root part. This is a safety net for bug #263:
        /// when a symmetry group of radial decouplers fires, individual <c>onPartJointBreak</c>
        /// events race with KSP's vessel-split processing, and some may be dropped mid-pipeline,
        /// leaving decoupled subtrees visible on the ghost during playback.
        ///
        /// The deferred scan is deterministic — every new debris vessel has exactly one root
        /// part, and that root is by construction the part that separated from the recording
        /// vessel, so emitting a <c>Decoupled</c> event for it hides the correct subtree.
        ///
        /// <para><b>Dedup source:</b> scans <see cref="PartEvents"/> directly for an existing
        /// <see cref="PartEventType.Decoupled"/> entry with the same pid, rather than the
        /// parallel <see cref="decoupledPartIds"/> tracking set. <c>decoupledPartIds</c>
        /// tracks "events OnPartJointBreak attempted to emit" but can drift out of sync
        /// with the actual serialized list if events are dropped or stripped downstream.
        /// Scanning the list is the only way to ensure the fallback recovers genuinely
        /// missing events. <c>PartEvents</c> is small at deferred-check time (~tens of
        /// entries), so the linear scan is negligible relative to the Unity physics cost
        /// that precedes it.</para>
        /// </summary>
        /// <param name="partPid">The persistent ID of the new vessel's root part.</param>
        /// <param name="partName">The root part's name (for logging/serialization).</param>
        /// <param name="ut">Universal time to stamp on the event.</param>
        /// <returns>1 if a new event was added, 0 if <see cref="PartEvents"/> already contains a Decoupled entry for this pid.</returns>
        internal int RecordFallbackDecoupleEvent(uint partPid, string partName, double ut)
        {
            // Dedup against the serialized list (source of truth), NOT the parallel
            // decoupledPartIds set. decoupledPartIds is a fast-path tracker for
            // OnPartJointBreak's own cycle-level dedup; it can be stale relative to
            // PartEvents if events were added there but then dropped or stripped
            // downstream. The fallback must check what will actually be serialized,
            // otherwise it silently skips the exact cases it was written to recover.
            for (int i = 0; i < PartEvents.Count; i++)
            {
                var existing = PartEvents[i];
                if (existing.eventType == PartEventType.Decoupled && existing.partPersistentId == partPid)
                {
                    ParsekLog.Verbose("Recorder",
                        $"RecordFallbackDecoupleEvent: pid={partPid} already has a Decoupled PartEvent, skipping");
                    return 0;
                }
            }

            // Keep decoupledPartIds in sync so any subsequent OnPartJointBreak for
            // the same pid (unlikely but defensive) short-circuits via its own fast path.
            decoupledPartIds.Add(partPid);

            PartEvents.Add(new PartEvent
            {
                ut = ut,
                partPersistentId = partPid,
                eventType = PartEventType.Decoupled,
                partName = partName ?? "unknown",
                value = 0f,
                moduleIndex = 0
            });
            ParsekLog.Info("Recorder",
                $"Fallback Decoupled event recorded: '{partName}' pid={partPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            return 1;
        }

        /// <summary>
        /// Determines whether a joint break is structural (real separation) or non-structural
        /// (e.g., wheel suspension stress). Only the structural attachJoint indicates real decoupling.
        /// </summary>
        /// <param name="brokenJointIsAttachJoint">True if the broken joint is the child's attachJoint.</param>
        /// <param name="hasAttachJoint">True if the child part has a non-null attachJoint (false for root parts).</param>
        /// <summary>
        /// Notes a part that comes off the recorded vessel through a clicked pre-attached
        /// docking port's Undock (stock routes it through Part.decouple, which fires
        /// onPartDeCouple first and never onPartUndock); see <see cref="DockingPortSeparation"/>.
        /// A staged port never arms the click note and stays a decouple.
        /// </summary>
        private void OnPartDeCouple(Part part)
        {
            if (!IsRecording || part?.vessel == null) return;
            if (part.vessel.persistentId != RecordingVesselId) return;
            if (!DockingPortSeparation.TryConsumePreAttachedUndock(
                    part.persistentId, Planetarium.GetUniversalTime(), "Recorder")) return;
            preAttachedUndockPartIds.Add(part.persistentId);
            ParsekLog.Verbose("Recorder",
                $"OnPartDeCouple: pre-attached docking-port undock part='{part.partInfo?.name}' " +
                $"pid={part.persistentId} vesselPid={RecordingVesselId} => split cause UNDOCK");
        }

        internal bool JointChildBrokeUnderForce(uint partPid)
            => partPid != 0 && forceBrokenJointChildPartIds.Contains(partPid);

        internal bool PartLeftThroughPreAttachedUndock(uint partPid)
            => partPid != 0 && preAttachedUndockPartIds.Contains(partPid);

        internal static bool IsStructuralJointBreak(bool brokenJointIsAttachJoint, bool hasAttachJoint)
        {
            // If the child has no attach joint (root part), any break is structural
            if (!hasAttachJoint) return true;
            // Only the structural attach joint indicates real separation
            return brokenJointIsAttachJoint;
        }

        private static string FormatCoverageEntries(List<string> entries, int maxEntries = 8)
        {
            if (entries == null || entries.Count == 0)
                return "<none>";

            if (entries.Count <= maxEntries)
                return string.Join(", ", entries.ToArray());

            var preview = entries.GetRange(0, maxEntries);
            return string.Join(", ", preview.ToArray()) + $", +{entries.Count - maxEntries} more";
        }

        private static void LogCoverageDetails(string label, List<string> entries)
        {
            ParsekLog.Verbose("Recorder", $"  Visual coverage [{label}] {entries.Count}: {FormatCoverageEntries(entries)}");
        }

        private sealed class VisualCoverageAccumulator
        {
            public int ModuleCount;
            public readonly List<string> ParachuteParts = new List<string>();
            public readonly List<string> JettisonModules = new List<string>();
            public readonly List<string> DeployableParts = new List<string>();
            public readonly List<string> LadderModules = new List<string>();
            public readonly List<string> AnimationGroupModules = new List<string>();
            public readonly List<string> AnimateGenericModules = new List<string>();
            public readonly List<string> LightParts = new List<string>();
            public readonly List<string> GearModules = new List<string>();
            public readonly List<string> CargoBayParts = new List<string>();
            public readonly List<string> FairingParts = new List<string>();
            public readonly List<string> AeroSurfaceModules = new List<string>();
            public readonly List<string> ControlSurfaceModules = new List<string>();
            public readonly List<string> RobotArmScannerModules = new List<string>();
            public readonly List<string> AnimateHeatModules = new List<string>();
            public readonly List<string> EngineModules = new List<string>();
            public readonly List<string> RcsModules = new List<string>();
            public readonly List<string> RoboticModules = new List<string>();
        }

        private void LogVisualRecordingCoverage(Vessel v)
        {
            if (v == null || v.parts == null)
                return;

            var coverage = new VisualCoverageAccumulator();

            CollectPartVisualCoverage(v, coverage);
            CollectCachedEngineVisualCoverage(coverage);
            CollectCachedRcsVisualCoverage(coverage);
            CollectCachedRoboticVisualCoverage(coverage);
            LogVisualCoverageSummary(v, coverage);
            LogVisualCoverageDetails(coverage);
        }

        private static void CollectPartVisualCoverage(
            Vessel v,
            VisualCoverageAccumulator coverage)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                string partName = p.partInfo?.name ?? "unknown";
                string partRef = $"{partName}[pid={p.persistentId}]";

                if (p.FindModuleImplementing<ModuleParachute>() != null)
                    coverage.ParachuteParts.Add(partRef);

                ModuleDeployablePart deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable != null)
                    coverage.DeployableParts.Add(
                        $"{partRef}(state={deployable.deployState})");

                if (p.FindModuleImplementing<ModuleLight>() != null)
                    coverage.LightParts.Add(partRef);

                ModuleCargoBay cargoBay = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargoBay != null)
                {
                    int cargoIdx = cargoBay.DeployModuleIndex;
                    ModuleAnimateGeneric cargoAnim = cargoIdx >= 0 && cargoIdx < p.Modules.Count
                        ? p.Modules[cargoIdx] as ModuleAnimateGeneric
                        : null;
                    float? limitStop = ResolveCargoBayDeployLimitStop(cargoAnim, cargoBay.closedPosition);
                    string limitText = limitStop.HasValue
                        ? $",limitStop={limitStop.Value.ToString("F2", CultureInfo.InvariantCulture)}"
                        : "";
                    coverage.CargoBayParts.Add(
                        $"{partRef}(deployIdx={cargoBay.DeployModuleIndex},closed={cargoBay.closedPosition:F2}{limitText})");
                }

                if (p.FindModuleImplementing<ModuleProceduralFairing>() != null)
                    coverage.FairingParts.Add(partRef);

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                bool hasDedicatedAnimateHandler =
                    deployable != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    cargoBay != null;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    coverage.ModuleCount++;

                    string moduleName = module.moduleName ?? string.Empty;

                    var jettison = module as ModuleJettison;
                    if (jettison != null && !string.IsNullOrWhiteSpace(jettison.jettisonName))
                    {
                        coverage.JettisonModules.Add(
                            $"{partRef}(midx={m},names={jettison.jettisonName})");
                    }

                    var wheel = module as ModuleWheels.ModuleWheelDeployment;
                    if (wheel != null)
                    {
                        coverage.GearModules.Add(
                            $"{partRef}(midx={m},state='{wheel.stateString}')");
                    }

                    if (string.Equals(moduleName, "RetractableLadder", StringComparison.Ordinal))
                    {
                        coverage.LadderModules.Add($"{partRef}(midx={m})");
                        hasRetractableLadder = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                    {
                        coverage.AnimationGroupModules.Add($"{partRef}(midx={m})");
                        hasAnimationGroup = true;
                    }

                    if (string.Equals(moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                    {
                        coverage.AeroSurfaceModules.Add($"{partRef}(midx={m})");
                        hasAeroSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                    {
                        coverage.ControlSurfaceModules.Add($"{partRef}(midx={m})");
                        hasControlSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                    {
                        coverage.RobotArmScannerModules.Add($"{partRef}(midx={m})");
                        hasRobotArmScanner = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                    {
                        coverage.AnimateHeatModules.Add($"{partRef}(midx={m})");
                        hasAnimateHeat = true;
                    }
                }

                if (hasDedicatedAnimateHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                    continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    coverage.AnimateGenericModules.Add(
                        $"{partRef}(midx={m},anim={animateModule.animationName})");
                }
            }
        }

        private void CollectCachedEngineVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    var (part, engine, moduleIndex) = cachedEngines[i];
                    if (part == null || engine == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    int thrustTransformCount = engine.thrustTransforms != null ? engine.thrustTransforms.Count : 0;
                    coverage.EngineModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},id={engineId},thrust={thrustTransformCount})");
                }
            }
        }

        private void CollectCachedRcsVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedRcsModules != null)
            {
                for (int i = 0; i < cachedRcsModules.Count; i++)
                {
                    var (part, rcs, moduleIndex) = cachedRcsModules[i];
                    if (part == null || rcs == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    coverage.RcsModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},thrusters={thrusterCount},forces={forceCount},power={rcs.thrusterPower:F1})");
                }
            }
        }

        private void CollectCachedRoboticVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedRoboticModules != null)
            {
                for (int i = 0; i < cachedRoboticModules.Count; i++)
                {
                    var (part, _, moduleIndex, moduleName) = cachedRoboticModules[i];
                    if (part == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    coverage.RoboticModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},module={moduleName})");
                }
            }
        }

        private static void LogVisualCoverageSummary(
            Vessel v,
            VisualCoverageAccumulator coverage)
        {
            ParsekLog.Verbose("Recorder", 
                $"Visual recording coverage for '{v.vesselName}' pid={v.persistentId}: " +
                $"parts={v.parts.Count} modules={coverage.ModuleCount} " +
                $"parachute={coverage.ParachuteParts.Count} jettison={coverage.JettisonModules.Count} deployable={coverage.DeployableParts.Count} " +
                $"ladder={coverage.LadderModules.Count} animationGroup={coverage.AnimationGroupModules.Count} animateGeneric={coverage.AnimateGenericModules.Count} " +
                $"lights={coverage.LightParts.Count} gear={coverage.GearModules.Count} cargoBay={coverage.CargoBayParts.Count} fairing={coverage.FairingParts.Count} " +
                $"aeroSurface={coverage.AeroSurfaceModules.Count} controlSurface={coverage.ControlSurfaceModules.Count} " +
                $"robotArmScanner={coverage.RobotArmScannerModules.Count} animateHeat={coverage.AnimateHeatModules.Count} " +
                $"engine={coverage.EngineModules.Count} rcs={coverage.RcsModules.Count} robotics={coverage.RoboticModules.Count}");
        }

        private static void LogVisualCoverageDetails(VisualCoverageAccumulator coverage)
        {
            LogCoverageDetails("Parachute", coverage.ParachuteParts);
            LogCoverageDetails("Jettison", coverage.JettisonModules);
            LogCoverageDetails("Deployable", coverage.DeployableParts);
            LogCoverageDetails("Ladder", coverage.LadderModules);
            LogCoverageDetails("AnimationGroup", coverage.AnimationGroupModules);
            LogCoverageDetails("AnimateGeneric", coverage.AnimateGenericModules);
            LogCoverageDetails("Light", coverage.LightParts);
            LogCoverageDetails("Gear", coverage.GearModules);
            LogCoverageDetails("CargoBay", coverage.CargoBayParts);
            LogCoverageDetails("Fairing", coverage.FairingParts);
            LogCoverageDetails("AeroSurface", coverage.AeroSurfaceModules);
            LogCoverageDetails("ControlSurface", coverage.ControlSurfaceModules);
            LogCoverageDetails("RobotArmScanner", coverage.RobotArmScannerModules);
            LogCoverageDetails("AnimateHeat", coverage.AnimateHeatModules);
            LogCoverageDetails("Engine", coverage.EngineModules);
            LogCoverageDetails("RCS", coverage.RcsModules);
            LogCoverageDetails("Robotics", coverage.RoboticModules);
        }

        /// <summary>
        /// Rebuilds the engine / RCS / robotic module caches from the live part list after KSP
        /// reports the recorded vessel's structure changed (staging, decouple, undock, dock,
        /// EVA-construction weld), then prunes the tracking-set keys belonging to parts that left.
        /// Keys for parts that STAYED are untouched, so an engine that was burning before the change
        /// is still known to be burning and no spurious transition is emitted.
        ///
        /// <para>
        /// Three defects this closes. (1) Parts that left keep live <c>PartModule</c> references in
        /// the cache; the per-poll ownership guard already refuses to read them, and this drops
        /// them entirely so the guard has nothing to do. (2) Parts that ARRIVED (an EVA-construction
        /// welded-on engine, a docked-on tug's RCS) were absent from a cache built at
        /// <c>StartRecording</c> and therefore emitted nothing at all for the rest of the flight.
        /// (3) Because the guard makes a departed booster's burnout unobservable, its key never left
        /// <c>activeEngineKeys</c> — so the terminal emit at the rails transition or at stop wrote an
        /// EngineShutdown into the PARENT recording for a pid that departed minutes earlier, landing
        /// at the TAIL where <c>RecordingOptimizer.IsInertPartEventForTailTrim</c> counts it as
        /// interesting and the #263 boring-tail trim is defeated.
        /// </para>
        /// <para>
        /// The prune is silent — no synthetic event for a departed pid. See
        /// <see cref="PruneDepartedTrackingKeys"/> for why it measures survival against
        /// <c>Vessel.parts</c> as well as the rebuilt caches, and why engine/RCS state is moved to
        /// <see cref="DepartedEngineThrottles"/> rather than dropped.
        /// </para>
        /// </summary>
        public void OnVesselWasModified(Vessel v)
        {
            if (!IsRecording || v == null) return;
            if (v.persistentId != RecordingVesselId) return;

            int engineCountBefore = cachedEngines?.Count ?? 0;
            int rcsCountBefore = cachedRcsModules?.Count ?? 0;
            int roboticCountBefore = cachedRoboticModules?.Count ?? 0;

            cachedEngines = CacheEngineModules(v);
            cachedRcsModules = CacheRcsModules(v);
            cachedRoboticModules = CacheRoboticModules(v);

            // allEngineKeys drives the #298 EngineShutdown-sentinel seeding, so a welded-on engine
            // has to join it or a later split/promotion seed would omit its sentinel.
            int newEngineKeys = 0;
            if (cachedEngines != null && allEngineKeys != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    var (part, engine, moduleIndex) = cachedEngines[i];
                    if (part == null || engine == null) continue;
                    if (allEngineKeys.Add(EncodeEngineKey(part.persistentId, moduleIndex)))
                        newEngineKeys++;
                }
            }

            var pruned = PruneDepartedTrackingKeys(
                CollectSurvivingTrackedPartPids(v),
                activeEngineKeys, lastThrottle, allEngineKeys,
                activeRcsKeys, lastRcsThrottle, rcsActiveFrameCount,
                activeRoboticKeys, lastRoboticPosition, lastRoboticSampleUT,
                departedEngineThrottles, departedRcsThrottles);

            // onVesselWasModified fires in bursts (staging, docking, part death, crew moves), and
            // most of them do not change the module counts at all. Report a real change at Info so
            // it is greppable, and the no-op rebuilds at rate-limited Verbose so they stay visible
            // without flooding.
            string summary =
                $"Module caches rebuilt after vessel modification: pid={v.persistentId} " +
                $"engines={engineCountBefore}->{cachedEngines?.Count ?? 0} " +
                $"rcs={rcsCountBefore}->{cachedRcsModules?.Count ?? 0} " +
                $"robotics={roboticCountBefore}->{cachedRoboticModules?.Count ?? 0} " +
                $"newEngineKeys={newEngineKeys} " +
                $"prunedDepartedKeys={pruned.Total} " +
                $"(engine={pruned.engineKeys} rcs={pruned.rcsKeys} robotic={pruned.roboticKeys}) " +
                $"departedCarryOver={departedEngineThrottles.Count + departedRcsThrottles.Count}";
            bool countsChanged =
                engineCountBefore != (cachedEngines?.Count ?? 0)
                || rcsCountBefore != (cachedRcsModules?.Count ?? 0)
                || roboticCountBefore != (cachedRoboticModules?.Count ?? 0)
                || newEngineKeys > 0
                || pruned.Total > 0;
            if (countsChanged)
                ParsekLog.Info("Recorder", summary);
            else
                ParsekLog.VerboseRateLimited("Recorder", "module-cache-rebuild-unchanged", summary, 5.0);
        }

        /// <summary>
        /// The pids the departed-key prune treats as still ours: every pid on the live
        /// <c>Vessel.parts</c> list UNIONED with every pid in the freshly rebuilt cache lists.
        ///
        /// <para>
        /// <c>Vessel.parts</c> is the load-bearing half. The cache lists only contain parts that
        /// currently expose an engine / RCS / robotic module, so a part whose module list reads empty
        /// for one frame (a module destroyed and re-added, a reflection probe that momentarily
        /// resolves nothing) drops out of them while the part itself never left the vessel. Pruning
        /// against the caches alone would turn that transient into a permanently forgotten burn —
        /// exactly the "momentarily vesselless during a dock/undock shuffle" hazard, resolved by
        /// asking the vessel's own part list rather than trusting the narrower derived view.
        /// </para>
        /// <para>
        /// The cache half is kept anyway so the union stays correct if <c>CacheEngineModules</c> and
        /// friends ever learn to include a part the vessel list does not.
        /// </para>
        /// </summary>
        private HashSet<uint> CollectSurvivingTrackedPartPids(Vessel v)
        {
            var pids = new HashSet<uint>();
            if (v?.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                    if (v.parts[i] != null) pids.Add(v.parts[i].persistentId);
            }
            if (cachedEngines != null)
                for (int i = 0; i < cachedEngines.Count; i++)
                    if (cachedEngines[i].part != null) pids.Add(cachedEngines[i].part.persistentId);
            if (cachedRcsModules != null)
                for (int i = 0; i < cachedRcsModules.Count; i++)
                    if (cachedRcsModules[i].part != null) pids.Add(cachedRcsModules[i].part.persistentId);
            if (cachedRoboticModules != null)
                for (int i = 0; i < cachedRoboticModules.Count; i++)
                    if (cachedRoboticModules[i].part != null) pids.Add(cachedRoboticModules[i].part.persistentId);
            return pids;
        }

        internal static bool IsWheelRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotor", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal);
        }

        internal static bool IsRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleRoboticServoHinge", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticRotationServo", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal) ||
                   IsWheelRoboticModuleName(moduleName);
        }

        /// <summary>
        /// The wheel MOTOR modules, whose spin is derived at playback from the ghost's own ground
        /// speed instead of being recorded. This is the VISUAL-MODE test (it maps to
        /// <c>RoboticVisualMode.WheelGroundSpeed</c>); the recorder's emission gate is the wider
        /// <see cref="IsDerivedWheelVisualModuleName"/>, which also covers ModuleWheelSteering.
        /// Returns false for ModuleWheelSuspension, whose scalars are still recorded normally, and
        /// for ModuleWheelSteering, whose caliper angle is a DIFFERENT derived visual
        /// (<c>WheelSteeringHeading</c>) and must not be spun like a motor.
        ///
        /// Why these two stopped being recorded: the only field on <c>ModuleWheelMotor</c> that the
        /// probe list could resolve is <c>driveOutput</c>, and <c>module.Fields</c> is
        /// <c>[KSPField]</c>-only, so every RPM-shaped candidate ahead of it (<c>currentRPM</c>,
        /// <c>rpm</c>, <c>wheelRPM</c>, <c>motorRPM</c>, <c>targetRPM</c>) resolves to nothing.
        /// Decompiled, <c>driveOutput</c> is
        /// <c>Mathf.Abs(driveInput * wheel.maxDriveTorque / maxTorque) * 100f * resourceFraction</c>
        /// — an UNSIGNED PERCENT OF MAX TORQUE, declared
        /// <c>[UI_ProgressBar(minValue = 0, maxValue = 100)]</c>. Playback spun the wheel at
        /// <c>value * 6</c> deg/s as if it were RPM, so: a coasting rover recorded 0 and showed
        /// STATIONARY wheels while moving; reverse was byte-identical to forward because of the
        /// <c>Mathf.Abs</c>; and the magnitude was a torque fraction, not a rate.
        ///
        /// Deriving the spin from ground speed at playback is strictly more correct on all three
        /// counts and is storage-NEGATIVE, so the signal is simply not recorded any more.
        ///
        /// ModuleWheelMotorSteering is included because it is a motor first: the field plan reaches
        /// <c>driveOutput</c> before its <c>steeringAngle</c> fallback, so what it recorded was the
        /// same bogus torque percent, not steering. (Recording real steering deflection is a separate
        /// piece of work — it would also need a visual mode other than continuous spin.)
        ///
        /// NOTE: this predicate gates EMISSION only. It must not be folded into
        /// <see cref="IsRoboticModuleName"/>, because that predicate assigns the sequential
        /// <c>roboticModuleIndex</c> in BOTH <see cref="CacheRoboticModules"/> and the ghost builder's
        /// <c>TryBuildRoboticInfos</c>, and those two numberings have to stay in lockstep — the
        /// playback key is <c>EncodeEngineKey(pid, moduleIndex)</c>. Skipping wheel motors there
        /// would renumber every later robotic module on the same part.
        /// </summary>
        internal static bool IsWheelMotorSpinModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleWheelMotor", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal);
        }

        /// <summary>
        /// The wheel modules whose visual is DERIVED from the ghost's own ground motion at playback,
        /// so recording a scalar for them is pure storage cost. This is the RECORDER EMISSION GATE
        /// (foreground <see cref="CheckRoboticState"/> and its background mirror); it is deliberately
        /// wider than <see cref="IsWheelMotorSpinModuleName"/>, which stays the visual-mode test.
        ///
        /// ModuleWheelSteering joins the motors for the same reason they left: what the field plan
        /// resolves on stock rovers is a steering INPUT (<c>steeringInput</c> / <c>currentSteering</c>,
        /// unsigned on several of them), not a caliper ANGLE, and playback replayed it as an angle.
        /// <c>GhostPlaybackLogic.ApplyRoboticEvent</c> now discards every RoboticMotion* event whose
        /// visual mode is <c>WheelSteeringHeading</c> and derives the caliper from the ghost's
        /// ground-track heading rate instead — a signal every recording carries, old ones included.
        /// So the producer had become a write-only surface: bytes on disk that no playback path can
        /// ever read. Deleting the emission is storage-NEGATIVE and behaviour-neutral.
        ///
        /// Nothing is retired in <c>PartEventType</c>: RoboticMotionStarted / RoboticPositionSample /
        /// RoboticMotionStopped are still the live signal for hinges, pistons, rotation servos, rotors
        /// and wheel SUSPENSION. Legacy recordings keep their steering events on disk and playback
        /// stays tolerant of them (it logs and ignores), so there is nothing to migrate.
        ///
        /// Like the motor gate, this one gates EMISSION ONLY. It must not be folded into
        /// <see cref="IsRoboticModuleName"/> — that predicate assigns the sequential
        /// <c>roboticModuleIndex</c> that the playback key <c>EncodeEngineKey(pid, moduleIndex)</c>
        /// is built from, in both <see cref="CacheRoboticModules"/> and the ghost builder, and the two
        /// numberings have to stay in lockstep.
        /// </summary>
        internal static bool IsDerivedWheelVisualModuleName(string moduleName)
        {
            return IsWheelMotorSpinModuleName(moduleName) ||
                   string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal);
        }

        internal static List<(Part part, PartModule module, int moduleIndex, string moduleName)> CacheRoboticModules(Vessel v)
        {
            var result = new List<(Part, PartModule, int, string)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int roboticModuleIndex = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;

                    string moduleName = module.moduleName;
                    if (!IsRoboticModuleName(moduleName)) continue;

                    result.Add((p, module, roboticModuleIndex, moduleName));
                    roboticModuleIndex++;
                }
            }

            return result;
        }

        private static BaseField FindModuleField(PartModule module, string fieldName)
        {
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return null;

            try
            {
                return module.Fields[fieldName];
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder", $"field-{fieldName}",
                    $"FindModuleField exception for '{fieldName}' on {module.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool TryParseFloatValue(object rawValue, out float value)
        {
            value = 0f;
            if (rawValue == null) return false;

            if (rawValue is float f)
            {
                value = f;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is double d)
            {
                value = (float)d;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is int i)
            {
                value = i;
                return true;
            }

            if (rawValue is uint ui)
            {
                value = ui;
                return true;
            }

            if (rawValue is bool b)
            {
                value = b ? 1f : 0f;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);

            // Some KSP fields serialize vectors/quaternions as comma-separated values.
            string[] parts = text.Split(',');
            if (parts.Length > 0 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            return false;
        }

        private static bool TryParseBoolValue(object rawValue, out bool value)
        {
            value = false;
            if (rawValue == null) return false;

            if (rawValue is bool b)
            {
                value = b;
                return true;
            }

            if (rawValue is int i)
            {
                value = i != 0;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (bool.TryParse(text, out value)) return true;

            int intValue;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                value = intValue != 0;
                return true;
            }

            return false;
        }

        private static bool TryParseVector3Value(object rawValue, out Vector3 value)
        {
            value = Vector3.zero;
            if (rawValue == null) return false;

            if (rawValue is Vector3 vec)
            {
                value = vec;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;

            float x;
            float y;
            float z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseQuaternionValue(object rawValue, out Quaternion value)
        {
            value = Quaternion.identity;
            if (rawValue == null) return false;

            if (rawValue is Quaternion quat)
            {
                value = quat;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 4) return false;

            float x;
            float y;
            float z;
            float w;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;

            value = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool TryReadModuleFloatField(PartModule module, string fieldName, out float value)
        {
            value = 0f;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseFloatValue(field.GetValue(module), out value);
        }

        private static float NormalizeAnimateHeatScalar(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw))
                return 0f;

            // Common case: ModuleAnimateHeat animTime style 0..1 scalar.
            if (raw >= -0.25f && raw <= 1.25f)
                return Mathf.Clamp01(raw);

            // Percent-like values.
            if (raw >= 0f && raw <= 100f)
                return Mathf.Clamp01(raw / 100f);

            // Absolute-temperature fallback (ambient ~300K, hot ~1500K+).
            if (raw >= 250f && raw <= 2500f)
                return Mathf.Clamp01((raw - 300f) / 1200f);

            return Mathf.Clamp01(raw);
        }

        private static bool TryReadModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseBoolValue(field.GetValue(module), out value);
        }

        private static bool TryReadModuleVector3Field(PartModule module, string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseVector3Value(field.GetValue(module), out value);
        }

        private static bool TryReadModuleQuaternionField(PartModule module, string fieldName, out Quaternion value)
        {
            value = Quaternion.identity;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseQuaternionValue(field.GetValue(module), out value);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyRoboticMovingFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryGetRoboticMovingState(PartModule module, out bool moving)
        {
            return TryClassifyRoboticMovingFromFieldValues(
                new PartModuleFieldValues(module), out moving);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyWheelRoboticPositionFromFieldValues{TFields}"/> core, which owns
        /// the per-module-name field plan and deadband.
        /// </summary>
        internal static bool TryGetWheelRoboticPositionValue(
            PartModule module,
            string moduleName,
            out float positionValue,
            out float deadband,
            out string sourceField)
        {
            return TryClassifyWheelRoboticPositionFromFieldValues(
                new PartModuleFieldValues(module), moduleName,
                out positionValue, out deadband, out sourceField);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyRoboticPositionFromFieldValues{TFields}"/> core, which owns the
        /// wheel dispatch, the per-module-name field plan and the deadband.
        /// </summary>
        internal static bool TryGetRoboticPositionValue(
            PartModule module, string moduleName, out float positionValue,
            out float deadband, out string sourceField)
        {
            return TryClassifyRoboticPositionFromFieldValues(
                new PartModuleFieldValues(module), moduleName,
                out positionValue, out deadband, out sourceField);
        }

        private static string DescribeModuleFields(PartModule module)
        {
            if (module == null || module.Fields == null) return "<none>";

            var names = new List<string>();
            for (int i = 0; i < module.Fields.Count; i++)
            {
                BaseField field = module.Fields[i];
                if (field == null || string.IsNullOrEmpty(field.name)) continue;
                names.Add(field.name);
            }

            return names.Count == 0 ? "<none>" : string.Join(", ", names.ToArray());
        }

        internal static List<PartEvent> CheckRoboticTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool movingSignal, float positionValue, float deadband, double ut,
            HashSet<ulong> movingSet, Dictionary<ulong, float> lastPositionMap,
            Dictionary<ulong, double> lastSampleMap, double sampleIntervalSeconds)
        {
            var events = new List<PartEvent>();
            bool emittedEvent = false;
            bool wasMoving = movingSet.Contains(key);

            float lastPosition;
            bool hadLastPosition = lastPositionMap.TryGetValue(key, out lastPosition);
            float delta = hadLastPosition ? Mathf.Abs(positionValue - lastPosition) : float.PositiveInfinity;
            bool changedEnough = !hadLastPosition || delta >= deadband;
            bool inferredMoving = hadLastPosition && delta >= Math.Max(deadband * 0.25f, 0.0001f);
            bool movingNow = movingSignal || inferredMoving;

            if (movingNow && !wasMoving)
            {
                movingSet.Add(key);
                lastSampleMap[key] = ut;
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStarted,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (!movingNow && wasMoving)
            {
                movingSet.Remove(key);
                lastSampleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStopped,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (movingNow && wasMoving)
            {
                double lastSampleUT;
                bool sampleDue = !lastSampleMap.TryGetValue(key, out lastSampleUT) ||
                    (ut - lastSampleUT) >= sampleIntervalSeconds;
                if (sampleDue && changedEnough)
                {
                    lastSampleMap[key] = ut;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.RoboticPositionSample,
                        partName = partName,
                        value = positionValue,
                        moduleIndex = moduleIndex
                    });
                    emittedEvent = true;
                }
            }

            if (emittedEvent || !hadLastPosition)
                lastPositionMap[key] = positionValue;

            return events;
        }

        private void CheckRoboticState(Vessel v)
        {
            if (cachedRoboticModules == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);
                if (!ShouldPollCachedModule(part, module, v, key, "robotic")) continue;

                // Wheel motor spin AND wheel steering deflection are derived from the ghost's own
                // ground motion at playback, not recorded. The module stays in cachedRoboticModules
                // so moduleIndex numbering matches the ghost builder's; only the emission is
                // skipped. See IsDerivedWheelVisualModuleName.
                if (IsDerivedWheelVisualModuleName(moduleName))
                {
                    if (loggedRoboticModuleKeys.Add(key))
                    {
                        ParsekLog.Verbose("Recorder", $"Robotics: derived wheel visual not recorded for " +
                            $"'{part.partInfo?.name}' pid={part.persistentId} midx={moduleIndex} " +
                            $"module={moduleName}; derived from ghost ground motion at playback");
                    }
                    continue;
                }

                bool hasMovingSignal = TryGetRoboticMovingState(module, out bool movingSignal);
                bool hasPosition = TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition)
                {
                    if (loggedRoboticModuleKeys.Add(key))
                    {
                        ParsekLog.Verbose("Recorder", $"Robotics: unable to sample '{part.partInfo?.name}' pid={part.persistentId} " +
                            $"midx={moduleIndex} module={moduleName}; fields=[{DescribeModuleFields(module)}]");
                    }
                    continue;
                }

                if (loggedRoboticModuleKeys.Add(key))
                {
                    string movingSource = hasMovingSignal ? "module-flag" : "inferred";
                    ParsekLog.Verbose("Recorder", $"Robotics: tracking '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} module={moduleName} source={sourceField} " +
                        $"deadband={deadband:F3} sampleHz=4.0 moving={movingSource}");
                }

                var events = CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    activeRoboticKeys, lastRoboticPosition, lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3}");
                }
            }
        }

        #endregion

        /// <summary>
        /// Resets all part event tracking state and rebuilds module caches for the given vessel.
        /// Extracted from StartRecording for reuse by the promotion path.
        /// </summary>
        /// <summary>
        /// Clears part-event tracking state, re-caches modules, seeds current state,
        /// and optionally emits seed events for the initial visual baseline.
        /// </summary>
        /// <param name="emitSeedEvents">True on new recording starts so ghost playback
        /// has an initial visual baseline (bugs #70/#65). False on chain continuation
        /// promotions — the prior chain segment already has the non-engine seed events,
        /// and emitting new DeployableExtended / LightOn / etc. at the promotion UT
        /// poisons FindLastInterestingUT (bug A / #263 sibling).
        /// When false, this method still emits ENGINE-ONLY seeds (EngineIgnited for
        /// running engines and EngineShutdown sentinels for dead/idle engines) when the
        /// active tree recording has zero engine events, so the ghost playback
        /// orphan-engine auto-start heuristic does not false-positive on Re-Fly forks,
        /// split branches, and merge branches that begin with empty PartEvents.
        /// Non-engine seeds are still skipped on promotion to preserve the bug A
        /// invariant — the orphan-engine guard only inspects engine events, so engine
        /// sentinels are the minimum needed to defeat it.</param>
        internal void ResetPartEventTrackingState(Vessel v, bool emitSeedEvents = true)
        {
            decoupledPartIds.Clear();
            forceBrokenJointChildPartIds.Clear();
            preAttachedUndockPartIds.Clear();
            parachuteStates.Clear();
            jettisonedShrouds.Clear();
            jettisonNameRawCache.Clear();
            parsedJettisonNamesCache.Clear();
            extendedDeployables.Clear();
            brokenDeployables.Clear();
            activeConverterParts.Clear();
            damagedWheelParts.Clear();
            jetpackDeployedParts.Clear();
            thrustingJetpackParts.Clear();
            ragdollParts.Clear();
            evaThrustFrameCounts.Clear();
            lightsOn.Clear();
            blinkingLights.Clear();
            lightBlinkRates.Clear();
            deployedGear.Clear();
            openCargoBays.Clear();
            deployedFairings.Clear();
            deployedLadders.Clear();
            deployedAnimationGroups.Clear();
            deployedAnimateGenericModules.Clear();
            deployedAeroSurfaceModules.Clear();
            deployedControlSurfaceModules.Clear();
            deployedRobotArmScannerModules.Clear();
            animateHeatLevels.Clear();
            loggedLadderClassificationMisses.Clear();
            loggedAnimationGroupClassificationMisses.Clear();
            loggedAnimateGenericClassificationMisses.Clear();
            loggedAeroSurfaceClassificationMisses.Clear();
            loggedControlSurfaceClassificationMisses.Clear();
            loggedRobotArmScannerClassificationMisses.Clear();
            loggedAnimateHeatClassificationMisses.Clear();
            loggedCargoBayDeployIndexIssues.Clear();
            loggedCargoBayAnimationIssues.Clear();
            loggedCargoBayClosedPositionIssues.Clear();
            loggedFairingReadFailures.Clear();
            cachedEngines = CacheEngineModules(v);
            activeEngineKeys = new HashSet<ulong>();
            allEngineKeys = new HashSet<ulong>();
            lastThrottle = new Dictionary<ulong, float>();
            loggedEngineModuleKeys.Clear();
            cachedRcsModules = CacheRcsModules(v);
            activeRcsKeys = new HashSet<ulong>();
            lastRcsThrottle = new Dictionary<ulong, float>();
            rcsActiveFrameCount.Clear();
            loggedRcsModuleKeys.Clear();
            cachedRoboticModules = CacheRoboticModules(v);
            activeRoboticKeys = new HashSet<ulong>();
            lastRoboticPosition = new Dictionary<ulong, float>();
            lastRoboticSampleUT = new Dictionary<ulong, double>();
            loggedRoboticModuleKeys = new HashSet<ulong>();
            loggedForeignCachedModuleKeys.Clear();
            departedEngineThrottles.Clear();
            departedRcsThrottles.Clear();
            RebuildHarvestConverterCache(v, "start");
            ParsekLog.Info("Recorder",
                $"Module caches seeded for vessel pid={v.persistentId}: engines={cachedEngines?.Count ?? 0}, " +
                $"rcs={cachedRcsModules?.Count ?? 0}, robotics={cachedRoboticModules?.Count ?? 0}, " +
                $"converters={cachedConverters?.Count ?? 0}");

            // Seed all tracking sets with current part state so we don't emit
            // false events at first poll (e.g., shrouds already jettisoned,
            // engines already running, lights already on, etc.)
            SeedExistingPartStates(v);

            if (!emitSeedEvents)
            {
                // Engine-event-aware promotion gate. The playback orphan-engine guard
                // (GhostPlaybackLogic.AutoStartOrphanEnginePlayback / BuildEngineEventKeySet)
                // only inspects engine events — if a chain-promoted recording reaches
                // playback with zero engine events, every engine on the ghost auto-starts
                // at full power, even ones the recorder captured as shut down. Non-engine
                // seeds (DeployableExtended, LightOn, ParachuteDeployed, etc.) are still
                // skipped on promotion to preserve the bug A / #263 FindLastInterestingUT
                // invariant — re-emitting them at a late promotion UT is what blocked
                // rover boring-tail trim. Engine-only seeds are scoped narrowly enough
                // that they cannot recreate that failure mode: every other recording type
                // (Quickload-resume, BG promote, normal continuation) lands on an
                // activeRec that already has engine events, so the engine gate skips too;
                // the empty-engine branch fires only for genuinely fresh chain branches
                // (Re-Fly fork, CreateSplitBranch, CreateMergeBranch) whose seed UT is
                // effectively the recording's start UT.
                string activeRecId = ActiveTree?.ActiveRecordingId;
                Recording activeRec = null;
                if (ActiveTree != null
                    && ActiveTree.Recordings != null
                    && !string.IsNullOrEmpty(activeRecId))
                {
                    ActiveTree.Recordings.TryGetValue(activeRecId, out activeRec);
                }
                if (!ChainPromotionShouldEmitEngineSeeds(activeRec, out int engineEventCount, out int totalEventCount))
                {
                    ParsekLog.Verbose("Recorder",
                        $"ResetPartEventTrackingState: skipping seed events (chain promotion, " +
                        $"activeRec='{activeRecId}' already has {engineEventCount} engine event(s) " +
                        $"of {totalEventCount} total)");
                }
                else
                {
                    EmitEngineOnlySeedEventsForPromotion(v, activeRec, activeRecId, totalEventCount);
                }
                // Converter seeds ride the same promotion gate shape: a fresh branch that
                // starts with a drill or ISRU already running needs its ConverterActivated,
                // or the tail-trim witness (CountConvertersRunningAtEnd) never sees the
                // converter and the trimmed tail delivers its resources early
                // (TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES).
                if (ChainPromotionShouldEmitConverterSeeds(activeRec, out int converterEventCount))
                    EmitConverterSeedEventsForPromotion(v, activeRec, activeRecId);
                else
                    ParsekLog.Verbose("Recorder",
                        $"ResetPartEventTrackingState: skipping converter seeds (chain promotion, " +
                        $"activeRec='{activeRecId}' already has {converterEventCount} converter event(s))");
                return;
            }

            // Emit seed events for the initial visual state so ghost playback
            // can reconstruct correct visuals from recording start (bugs #70/#65).
            var partNamesByPid = new Dictionary<uint, string>();
            if (v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                        partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                }
            }
            var seedSets = BuildCurrentTrackingSets();
            double seedUT = Planetarium.GetUniversalTime();
            var seedEvents = PartStateSeeder.EmitSeedEvents(seedSets, partNamesByPid, seedUT, "Recorder");
            PartEvents.AddRange(seedEvents);
        }

        /// <summary>
        /// Emits engine-only seed events (EngineIgnited / EngineShutdown sentinels) into
        /// the recorder's PartEvents buffer for the chain-promotion empty-engine branch
        /// of <see cref="ResetPartEventTrackingState"/>. Other seed types are intentionally
        /// not emitted here — see the promotion gate's bug A / #263 rationale.
        ///
        /// Seed UT is anchored to <see cref="Recording.StartUT"/> when the active recording
        /// has established trajectory data (i.e. a populated recording being resumed) and
        /// falls back to <see cref="Planetarium.GetUniversalTime"/> for genuinely fresh
        /// chain branches whose StartUT is not yet populated. The StartUT anchor closes
        /// the bug A / #263 hole that EngineShutdown sentinels are non-inert in
        /// <see cref="RecordingOptimizer.IsInertPartEventForTailTrim"/>: if a resume
        /// landed on a recording with no engine events but live engine parts, emitting
        /// sentinels at the resume UT would still move FindLastInterestingUT to the
        /// resume UT and block boring-tail trim. Anchoring at the recording's actual
        /// start UT (which is in the past for any resume) keeps the sentinels
        /// dominated by later real activity.
        /// </summary>
        private void EmitEngineOnlySeedEventsForPromotion(
            Vessel v, Recording activeRec, string activeRecId, int totalEventCount)
        {
            var partNamesByPid = new Dictionary<uint, string>();
            if (v != null && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                        partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                }
            }
            string NameFor(uint pid)
            {
                return partNamesByPid.TryGetValue(pid, out string n) ? n : "unknown";
            }
            var seedSets = BuildCurrentTrackingSets();
            double currentUT = Planetarium.GetUniversalTime();
            double seedUT = ResolveChainPromotionSeedUT(activeRec, currentUT);
            var engineSeedEvents = new List<PartEvent>();
            PartStateSeeder.EmitEngineSeedEvents(seedSets, engineSeedEvents, seedUT, "Recorder", NameFor);
            if (engineSeedEvents.Count > 0)
                PartEvents.AddRange(engineSeedEvents);
            ParsekLog.Verbose("Recorder",
                $"ResetPartEventTrackingState: emitted {engineSeedEvents.Count} engine-only " +
                $"seed event(s) for chain promotion (activeRec='{activeRecId ?? "(none)"}' had 0 " +
                $"engine events of {totalEventCount} total — seedUT={seedUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"currentUT={currentUT.ToString("F2", CultureInfo.InvariantCulture)} — " +
                $"non-engine seeds still skipped to preserve bug A / #263 invariant)");
        }

        /// <summary>
        /// Emits ConverterActivated seeds for the converters running at a chain promotion,
        /// at the same UT the engine seeds use (<see cref="ResolveChainPromotionSeedUT"/>:
        /// the recording's StartUT when it already has trajectory, else now), so a seed never
        /// moves FindLastInterestingUT past real activity.
        /// </summary>
        private void EmitConverterSeedEventsForPromotion(Vessel v, Recording activeRec, string activeRecId)
        {
            var partNamesByPid = new Dictionary<uint, string>();
            if (v != null && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                        partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                }
            }
            var seedSets = BuildCurrentTrackingSets();
            double currentUT = Planetarium.GetUniversalTime();
            double seedUT = ResolveChainPromotionSeedUT(activeRec, currentUT);
            var converterSeedEvents = new List<PartEvent>();
            int emitted = PartStateSeeder.EmitConverterSeedEvents(
                seedSets, converterSeedEvents, seedUT, "Recorder",
                pid => partNamesByPid.TryGetValue(pid, out string n) ? n : "unknown");
            if (emitted > 0)
                PartEvents.AddRange(converterSeedEvents);
            ParsekLog.Verbose("Recorder",
                $"ResetPartEventTrackingState: emitted {emitted} converter seed event(s) for chain " +
                $"promotion (activeRec='{activeRecId ?? "(none)"}' had 0 converter events - " +
                $"seedUT={seedUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"currentUT={currentUT.ToString("F2", CultureInfo.InvariantCulture)})");
        }

        /// <summary>
        /// Pure: whether a chain promotion emits ConverterActivated seeds. True when the
        /// active recording has no converter event yet (ConverterActivated /
        /// ConverterDeactivated): a fresh branch (split, merge, switch segment) that the
        /// promotion path would otherwise start with no record of a converter already
        /// running. A recording that already carries a converter edge keeps its own state.
        /// </summary>
        internal static bool ChainPromotionShouldEmitConverterSeeds(
            Recording activeRec, out int converterEventCount)
        {
            converterEventCount = 0;
            if (activeRec == null || activeRec.PartEvents == null)
                return true;
            for (int i = 0; i < activeRec.PartEvents.Count; i++)
            {
                var t = activeRec.PartEvents[i].eventType;
                if (t == PartEventType.ConverterActivated || t == PartEventType.ConverterDeactivated)
                    converterEventCount++;
            }
            return converterEventCount == 0;
        }

        /// <summary>
        /// Decides whether <see cref="ResetPartEventTrackingState"/> should emit
        /// engine-only seed events during a chain promotion
        /// (<c>emitSeedEvents=false</c> call path).
        ///
        /// Returns true when the active tree recording has zero engine events
        /// (EngineIgnited / EngineThrottle / EngineShutdown). Matches the contract of
        /// <see cref="GhostPlaybackLogic.BuildEngineEventKeySet"/> — the orphan-engine
        /// auto-start heuristic only inspects those three event types, and Re-Fly
        /// forks / split / merge branches that begin with empty PartEvents (or with
        /// only non-engine events like <c>LightOn</c>) would still trip the heuristic
        /// without engine seeds.
        ///
        /// Returns false when the active recording already has at least one engine
        /// event — the prior chain segment / pre-quickload flight has covered the
        /// orphan guard, and emitting engine sentinels at the promotion UT would
        /// duplicate state that the orphan guard does not need duplicated.
        ///
        /// <paramref name="totalEventCount"/> is reported alongside the engine count
        /// for diagnostic logging; only the engine count drives the decision.
        ///
        /// Pure static so it can be unit tested without a live <see cref="RecordingTree"/>.
        /// </summary>
        internal static bool ChainPromotionShouldEmitEngineSeeds(
            Recording activeRec, out int engineEventCount, out int totalEventCount)
        {
            engineEventCount = 0;
            totalEventCount = 0;
            if (activeRec == null || activeRec.PartEvents == null)
                return true;
            totalEventCount = activeRec.PartEvents.Count;
            for (int i = 0; i < activeRec.PartEvents.Count; i++)
            {
                var t = activeRec.PartEvents[i].eventType;
                if (t == PartEventType.EngineIgnited
                    || t == PartEventType.EngineThrottle
                    || t == PartEventType.EngineShutdown)
                {
                    engineEventCount++;
                }
            }
            return engineEventCount == 0;
        }

        /// <summary>
        /// Resolves the UT to stamp on engine seed events emitted by the
        /// chain-promotion empty-engine branch of <see cref="ResetPartEventTrackingState"/>.
        ///
        /// For a recording with established trajectory content (at least one Point,
        /// OrbitSegment, or playable TrackSection — see
        /// <see cref="Recording.HasActualTrajectoryBounds"/>), returns the recording's
        /// <see cref="Recording.StartUT"/> so the sentinels anchor at the recording's
        /// actual start, never moving FindLastInterestingUT forward against later real
        /// activity. 0.0 is treated as a valid anchor when it comes from real
        /// trajectory data (sandbox-epoch starts, debug worlds) — only the fallback
        /// 0.0 from an empty recording reroutes to currentUT, which IS the fork's
        /// start moment by definition.
        ///
        /// This is the bug A / #263 fix for non-inert EngineShutdown sentinels:
        /// without the StartUT anchor, a quickload-resume of an empty-engine
        /// recording would stamp sentinels at the resume UT and block tail trim.
        ///
        /// Pure static so it can be unit tested without a live recording or
        /// <see cref="Planetarium"/>.
        /// </summary>
        internal static double ResolveChainPromotionSeedUT(Recording activeRec, double currentUT)
        {
            if (activeRec == null)
                return currentUT;
            // Discriminate "real anchor at 0.0" (sandbox-epoch trajectory) from
            // "fallback default of 0.0" (empty recording / ExplicitStartUT-only).
            // Recording.StartUT cannot distinguish those by value, so we check the
            // recording's actual content instead.
            if (!activeRec.HasActualTrajectoryBounds)
                return currentUT;
            double recStartUT = activeRec.StartUT;
            if (double.IsNaN(recStartUT) || double.IsInfinity(recStartUT))
                return currentUT;
            // If the recording's StartUT is at or after the current UT, treat the
            // recording as fresh — the current frame IS its start moment.
            if (recStartUT >= currentUT)
                return currentUT;
            return recStartUT;
        }

        /// <summary>
        /// Builds the PartTrackingSets struct from current instance tracking state.
        /// Extracted to deduplicate the identical struct literal in ResetPartEventTrackingState
        /// and SeedExistingPartStates.
        /// </summary>
        private PartTrackingSets BuildCurrentTrackingSets()
        {
            return new PartTrackingSets
            {
                deployedFairings = deployedFairings,
                jettisonedShrouds = jettisonedShrouds,
                parachuteStates = parachuteStates,
                extendedDeployables = extendedDeployables,
                brokenDeployables = brokenDeployables,
                activeConverterParts = activeConverterParts,
                damagedWheelParts = damagedWheelParts,
                jetpackDeployedParts = jetpackDeployedParts,
                ragdollParts = ragdollParts,
                lightsOn = lightsOn,
                blinkingLights = blinkingLights,
                lightBlinkRates = lightBlinkRates,
                deployedGear = deployedGear,
                openCargoBays = openCargoBays,
                deployedLadders = deployedLadders,
                deployedAnimationGroups = deployedAnimationGroups,
                deployedAnimateGenericModules = deployedAnimateGenericModules,
                deployedAeroSurfaceModules = deployedAeroSurfaceModules,
                deployedControlSurfaceModules = deployedControlSurfaceModules,
                deployedRobotArmScannerModules = deployedRobotArmScannerModules,
                animateHeatLevels = animateHeatLevels,
                activeEngineKeys = activeEngineKeys,
                allEngineKeys = allEngineKeys,
                lastThrottle = lastThrottle,
                activeRcsKeys = activeRcsKeys,
                lastRcsThrottle = lastRcsThrottle,
            };
        }

        /// <summary>
        /// Pre-populates all part-event tracking sets with the current state of
        /// every part on the vessel. This prevents spurious events on the first
        /// physics-frame poll when recording starts mid-flight (e.g., chain
        /// continuation after staging, or background recorder initialization).
        /// </summary>
        internal void SeedExistingPartStates(Vessel v)
        {
            PartStateSeeder.SeedPartStates(v,
                BuildCurrentTrackingSets(),
                cachedEngines, cachedRcsModules,
                seedColorChangerLights: true, logTag: "Recorder");
        }
    }
}
