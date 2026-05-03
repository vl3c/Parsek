using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal struct ReFlyDisplayAlignment
    {
        public string SessionId;
        public string TreeId;
        public string RecordingId;
        public string BodyName;
        public Vector3d BodyFixedOffset;
        public double CaptureUT;
        public string LiveAnchorSource;
        public uint LiveAnchorPartPid;
        public double LiveAnchorVesselOffsetMeters;
        public double InitialWorldOffsetMeters;
        public bool DebobEnabled;
        public bool DebobReferenceCaptured;
        public Vector3d DebobReferenceCorrection;
        public double DebobReferenceCorrectionMeters;
        public double DebobReferenceUT;
        public bool GhostPartPinCaptured;
        public uint GhostPartPinPid;
        public Vector3d GhostPartPinBodyFixedOffset;
        public double GhostPartPinMeters;
        public double GhostPartPinUT;

        internal static bool TryCapture(
            string sessionId,
            string treeId,
            string recordingId,
            string bodyName,
            Quaternion bodyWorldRotation,
            Vector3d liveAnchorWorld,
            Vector3d recordedAnchorWorld,
            double captureUT,
            string liveAnchorSource,
            uint liveAnchorPartPid,
            double liveAnchorVesselOffsetMeters,
            out ReFlyDisplayAlignment alignment)
        {
            alignment = default(ReFlyDisplayAlignment);
            if (string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(bodyName)
                || !IsFinite(liveAnchorWorld)
                || !IsFinite(recordedAnchorWorld))
            {
                return false;
            }

            Vector3d worldDelta = liveAnchorWorld - recordedAnchorWorld;
            if (!IsFinite(worldDelta))
                return false;

            Vector3 local = TrajectoryMath.PureRotateVector(
                TrajectoryMath.PureInverse(TrajectoryMath.PureNormalize(bodyWorldRotation)),
                (Vector3)worldDelta);
            Vector3d bodyFixedOffset = new Vector3d(local.x, local.y, local.z);
            if (!IsFinite(bodyFixedOffset))
                return false;

            alignment = new ReFlyDisplayAlignment
            {
                SessionId = sessionId,
                TreeId = treeId,
                RecordingId = recordingId,
                BodyName = bodyName,
                BodyFixedOffset = bodyFixedOffset,
                CaptureUT = captureUT,
                LiveAnchorSource = liveAnchorSource,
                LiveAnchorPartPid = liveAnchorPartPid,
                LiveAnchorVesselOffsetMeters = liveAnchorVesselOffsetMeters,
                InitialWorldOffsetMeters = worldDelta.magnitude,
            };
            return true;
        }

        internal bool TryProject(Quaternion bodyWorldRotation, out Vector3d worldOffset)
        {
            Vector3 projected = TrajectoryMath.PureRotateVector(
                TrajectoryMath.PureNormalize(bodyWorldRotation),
                new Vector3(
                    (float)BodyFixedOffset.x,
                    (float)BodyFixedOffset.y,
                    (float)BodyFixedOffset.z));
            worldOffset = new Vector3d(projected.x, projected.y, projected.z);
            return IsFinite(worldOffset);
        }

        internal bool TryCaptureDebobReference(
            Quaternion bodyWorldRotation,
            Vector3d debobCorrection,
            double referenceUT)
        {
            if (!IsFinite(debobCorrection))
                return false;

            Vector3 local = TrajectoryMath.PureRotateVector(
                TrajectoryMath.PureInverse(TrajectoryMath.PureNormalize(bodyWorldRotation)),
                (Vector3)debobCorrection);
            Vector3d localCorrection = new Vector3d(local.x, local.y, local.z);
            if (!IsFinite(localCorrection))
                return false;

            BodyFixedOffset -= localCorrection;
            if (!IsFinite(BodyFixedOffset))
                return false;

            DebobReferenceCaptured = true;
            DebobReferenceCorrection = debobCorrection;
            DebobReferenceCorrectionMeters = debobCorrection.magnitude;
            DebobReferenceUT = referenceUT;
            return true;
        }

        internal bool TryApplyGhostPartPin(
            Quaternion bodyWorldRotation,
            Vector3d pinWorldDelta,
            uint partPersistentId,
            double pinUT)
        {
            if (partPersistentId == 0u || GhostPartPinCaptured)
                return false;
            if (!IsFinite(pinWorldDelta))
                return false;

            Vector3 local = TrajectoryMath.PureRotateVector(
                TrajectoryMath.PureInverse(TrajectoryMath.PureNormalize(bodyWorldRotation)),
                (Vector3)pinWorldDelta);
            Vector3d localPin = new Vector3d(local.x, local.y, local.z);
            if (!IsFinite(localPin))
                return false;

            BodyFixedOffset += localPin;
            if (!IsFinite(BodyFixedOffset))
                return false;

            GhostPartPinCaptured = true;
            GhostPartPinPid = partPersistentId;
            GhostPartPinBodyFixedOffset = localPin;
            GhostPartPinMeters = pinWorldDelta.magnitude;
            GhostPartPinUT = pinUT;
            return true;
        }

        private static bool IsFinite(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }
    }

    internal sealed class ReFlyDisplayAlignmentCache
    {
        private readonly Dictionary<string, ReFlyDisplayAlignment> alignmentsByRecordingId =
            new Dictionary<string, ReFlyDisplayAlignment>(StringComparer.Ordinal);
        private string activeSessionId;
        private string activeScopeKey;

        internal int Count
        {
            get { return alignmentsByRecordingId.Count; }
        }

        internal void Clear()
        {
            alignmentsByRecordingId.Clear();
            activeSessionId = null;
            activeScopeKey = null;
        }

        internal void ClearIfSessionChanged(string sessionId)
        {
            ClearIfScopeChanged(sessionId, sessionId);
        }

        internal void ClearIfScopeChanged(string sessionId, string scopeKey)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                Clear();
                return;
            }

            string normalizedScopeKey = !string.IsNullOrEmpty(scopeKey)
                ? scopeKey
                : sessionId;
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(activeScopeKey, normalizedScopeKey, StringComparison.Ordinal))
            {
                alignmentsByRecordingId.Clear();
                activeSessionId = sessionId;
                activeScopeKey = normalizedScopeKey;
            }
        }

        internal bool TryGet(string sessionId, string recordingId, out ReFlyDisplayAlignment alignment)
        {
            alignment = default(ReFlyDisplayAlignment);
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(recordingId))
                return false;
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
                return false;
            return alignmentsByRecordingId.TryGetValue(recordingId, out alignment);
        }

        internal void Store(ReFlyDisplayAlignment alignment)
        {
            if (string.IsNullOrEmpty(alignment.SessionId)
                || string.IsNullOrEmpty(alignment.RecordingId))
                return;

            if (!string.Equals(activeSessionId, alignment.SessionId, StringComparison.Ordinal))
            {
                alignmentsByRecordingId.Clear();
                activeScopeKey = alignment.SessionId;
            }

            activeSessionId = alignment.SessionId;
            if (string.IsNullOrEmpty(activeScopeKey))
                activeScopeKey = alignment.SessionId;
            alignmentsByRecordingId[alignment.RecordingId] = alignment;
        }

        internal bool Remove(string sessionId, string recordingId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(recordingId))
                return false;
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
                return false;
            return alignmentsByRecordingId.Remove(recordingId);
        }
    }
}
