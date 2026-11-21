using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// ServoMirroredPose (lane SRV-3): the two halves of a Breaking Ground hinge symmetry pair,
    /// one carrying <c>mirrorRotation = True</c>, must spawn and play on the ghost as mirror
    /// images, at the pose stock itself persisted. Stock poses a hinge at
    /// <c>(mirrorRotation ? -targetAngle : targetAngle) - modelInitialAngle</c> (decompiled
    /// ModuleRoboticServoHinge.JointTargetAngle, KSP 1.12.5); the ghost's snapshot baseline used
    /// to read the raw <c>targetAngle</c>, so the mirrored half started its replay turned the
    /// wrong way until its first recorded event (GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle).
    ///
    /// <para>
    /// The subject is the COMMITTED recording SRV-3 flew (the first in ERS whose start snapshot
    /// carries a mirrored hinge pair); the cell builds its ghost through the real pipeline from
    /// that stock-written snapshot and reads the two moving transforms twice: at spawn (the
    /// snapshot baseline, the fix) and at a sampled UT, the last settled stop of either half,
    /// after replaying the pair's recorded robotic events through the real applier. Every other
    /// posed servo on the same ghost (SRV-3's offset Alligator hinge) is checked against stock's
    /// persisted pose at spawn too, which is the live check of the <c>modelInitialAngle</c>
    /// offset. Without the subject the cell self-skips, so the lane's pinned tally proves the
    /// flight produced it. The pure halves are tested headless in ServoMirroredPoseHelpersTests.
    /// </para>
    /// </summary>
    public class ServoMirroredPoseInGameTests
    {
        private readonly InGameTestRunner runner;

        public ServoMirroredPoseInGameTests(InGameTestRunner runner)
        {
            this.runner = runner;
        }

        internal const string HingeModuleName = "ModuleRoboticServoHinge";

        // The spawn baseline is built from the snapshot's COMMANDED targetAngle on both halves, so
        // the two poses are exact negatives up to float quaternion round-off (~1e-5 deg); the
        // defect class is a sign error, twice the target (120 deg on SRV-3).
        internal const float SpawnMirrorToleranceDegrees = 0.05f;
        // Stock persists the LIVE moving transform (servoTransformRotation), which sits within the
        // drive's static error of the commanded angle once settled; with the pivot axis level that
        // error is well under the recorder's 0.5 deg deadband. The defect class is 90 deg (a lost
        // modelInitialAngle) or twice the target (a lost mirror sign).
        internal const float StockPoseToleranceDegrees = 1.0f;
        // The sampled poses are each half's own recorded Stopped value, a live reading after the
        // 0.25 s stop hold, so the pair mirrors only within the drive's settle (same bound).
        internal const float SampleMirrorToleranceDegrees = 1.0f;
        // A replayed event poses the recorded value exactly (one AngleAxis), float round-off only.
        internal const float RecordedValueToleranceDegrees = 0.05f;
        // Below this a pair is at rest and the mirror check says nothing.
        internal const float MinPosedDegrees = 1.0f;

        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>The two halves of one hinge symmetry pair in a vessel snapshot.</summary>
        internal struct MirroredHingePair
        {
            public string partName;
            public uint plainPid;
            public uint mirroredPid;
            public float plainTarget;
            public float mirroredTarget;
            public bool linkedBySym;
        }

        private struct HingePart
        {
            public int index;
            public string name;
            public uint pid;
            public bool mirror;
            public float target;
            public List<int> sym;
        }

        /// <summary>
        /// Pure: finds the first hinge pair in <paramref name="vesselSnapshot"/> whose halves
        /// share a part name, one with <c>mirrorRotation = True</c> and one without, each with the
        /// hinge as its robotic ordinal 0 and a readable <c>targetAngle</c>. A pair linked by the
        /// snapshot's own <c>sym</c> indices wins; otherwise the first same-name unmirrored hinge.
        /// </summary>
        internal static bool TryFindMirroredHingePair(ConfigNode vesselSnapshot, out MirroredHingePair pair)
        {
            pair = default(MirroredHingePair);
            ConfigNode[] parts = vesselSnapshot != null ? vesselSnapshot.GetNodes("PART") : null;
            if (parts == null) return false;

            var hinges = new List<HingePart>();
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode part = parts[i];
                if (part == null) continue;
                if (!uint.TryParse(part.GetValue("persistentId"), NumberStyles.None, ic, out uint pid)
                    || pid == 0)
                    continue;
                ConfigNode[] modules = part.GetNodes("MODULE");
                ConfigNode first = null;
                for (int m = 0; modules != null && m < modules.Length; m++)
                {
                    if (FlightRecorder.IsRoboticModuleName(modules[m]?.GetValue("name")))
                    {
                        first = modules[m];
                        break;
                    }
                }
                if (first == null || first.GetValue("name") != HingeModuleName) continue;
                if (!float.TryParse(first.GetValue("targetAngle"), NumberStyles.Float, ic, out float target)
                    || float.IsNaN(target) || float.IsInfinity(target))
                    continue;
                bool.TryParse(first.GetValue("mirrorRotation"), out bool mirror);
                var sym = new List<int>();
                string[] symValues = part.GetValues("sym");
                for (int s = 0; symValues != null && s < symValues.Length; s++)
                    if (int.TryParse(symValues[s], NumberStyles.Integer, ic, out int link)) sym.Add(link);
                hinges.Add(new HingePart
                {
                    index = i, name = part.GetValue("name"), pid = pid, mirror = mirror,
                    target = target, sym = sym,
                });
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool requireSym = pass == 0;
                for (int a = 0; a < hinges.Count; a++)
                {
                    if (!hinges[a].mirror) continue;
                    for (int b = 0; b < hinges.Count; b++)
                    {
                        HingePart plain = hinges[b];
                        HingePart mirrored = hinges[a];
                        if (plain.mirror || plain.name != mirrored.name) continue;
                        bool linked = mirrored.sym.Contains(plain.index) || plain.sym.Contains(mirrored.index);
                        if (requireSym && !linked) continue;
                        pair = new MirroredHingePair
                        {
                            partName = plain.name,
                            plainPid = plain.pid,
                            mirroredPid = mirrored.pid,
                            plainTarget = plain.target,
                            mirroredTarget = mirrored.target,
                            linkedBySym = linked,
                        };
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: the signed turn, in degrees in (-180, 180], from <paramref name="rest"/> to
        /// <paramref name="current"/> about <paramref name="axis"/> (both local rotations of one
        /// transform; the turn is <c>Inverse(rest) * current</c>, a unit quaternion's inverse
        /// being its conjugate). NaN for a zero axis.
        /// </summary>
        internal static float SignedAngleAboutAxisDegrees(Quaternion rest, Quaternion current, Vector3 axis)
        {
            float len = Mathf.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            if (len < 1e-6f) return float.NaN;
            var inverse = new Quaternion(-rest.x, -rest.y, -rest.z, rest.w);
            Quaternion delta = inverse * current;
            float along = (delta.x * axis.x + delta.y * axis.y + delta.z * axis.z) / len;
            float degrees = 2f * Mathf.Atan2(along, delta.w) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(0f, degrees);
        }

        /// <summary>Pure: how far two displayed angles are from being each other's negative.</summary>
        internal static float MirrorResidualDegrees(float a, float b)
        {
            return Mathf.Abs(Mathf.DeltaAngle(a, -b));
        }

        internal static bool IsRoboticPoseEvent(PartEventType type)
        {
            return type == PartEventType.RoboticMotionStarted
                || type == PartEventType.RoboticPositionSample
                || type == PartEventType.RoboticMotionStopped;
        }

        /// <summary>
        /// Pure: the sampled UT for the pair, the later of the two halves' LAST
        /// RoboticMotionStopped on robotic ordinal 0 (both halves settled), and each half's last
        /// recorded value at or before it. False when either half never stopped.
        /// </summary>
        internal static bool TryResolveSettledSampleUT(IList<PartEvent> events, uint pidA, uint pidB,
            out double sampleUT, out float lastA, out float lastB)
        {
            sampleUT = double.NaN;
            lastA = float.NaN;
            lastB = float.NaN;
            if (events == null) return false;
            double stopA = double.NegativeInfinity, stopB = double.NegativeInfinity;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                if (e.moduleIndex != 0 || e.eventType != PartEventType.RoboticMotionStopped) continue;
                if (e.partPersistentId == pidA && e.ut > stopA) stopA = e.ut;
                if (e.partPersistentId == pidB && e.ut > stopB) stopB = e.ut;
            }
            if (double.IsNegativeInfinity(stopA) || double.IsNegativeInfinity(stopB)) return false;
            sampleUT = System.Math.Max(stopA, stopB);
            double atA = double.NegativeInfinity, atB = double.NegativeInfinity;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                if (e.moduleIndex != 0 || !IsRoboticPoseEvent(e.eventType) || e.ut > sampleUT) continue;
                if (e.partPersistentId == pidA && e.ut >= atA) { atA = e.ut; lastA = e.value; }
                if (e.partPersistentId == pidB && e.ut >= atB) { atB = e.ut; lastB = e.value; }
            }
            return !float.IsNaN(lastA) && !float.IsNaN(lastB);
        }

        private static float DisplayedAngle(RoboticGhostInfo info)
        {
            return Mathf.DeltaAngle(0f, SignedAngleAboutAxisDegrees(
                info.stowedRot, info.servoTransform.localRotation, info.axisLocal)
                + info.poseAngleOffsetDegrees);
        }

        private static string F(float v) => v.ToString("F2", ic);

        [InGameTest(Category = "ServoMirroredPose", Scene = GameScenes.FLIGHT,
            Description = "The ghost of a committed recording with a mirrored hinge symmetry pair spawns both halves as mirror images at stock's persisted pose, every other posed servo at stock's pose too, and stays mirrored after the pair's recorded swing replays to its last settled stop")]
        public void MirroredHingePairSpawnsAndPlaysAsMirrorImages()
        {
            Recording rec = null;
            MirroredHingePair pair = default(MirroredHingePair);
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; ers != null && i < ers.Count && rec == null; i++)
            {
                if (ers[i]?.GhostVisualSnapshot == null) continue;
                if (TryFindMirroredHingePair(ers[i].GhostVisualSnapshot, out pair))
                    rec = ers[i];
            }
            if (rec == null)
            {
                InGameAssert.Skip("no committed recording's start snapshot carries a Breaking Ground "
                    + "hinge symmetry pair with one half mirrorRotation = True (this cell needs lane "
                    + "SRV-3's recorded rover)");
                return;
            }

            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                rec, rec.GhostVisualSnapshot, "ParsekTest_ServoMirroredPose",
                HeaviestSpawnBuildType.RecordingStartSnapshot);
            InGameAssert.IsNotNull(build, $"recording {rec.RecordingId} should start a ghost build");
            GameObject cleanupRoot = build.root;
            GhostVisualBuilder.AdvanceTimelineGhostBuild(build, long.MaxValue);
            GhostBuildResult result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, null);
            if (result == null || result.root == null)
            {
                if (cleanupRoot != null && runner != null) runner.TrackForCleanup(cleanupRoot);
                InGameAssert.Fail($"recording {rec.RecordingId} produced no ghost visuals");
                return;
            }
            if (runner != null) runner.TrackForCleanup(result.root);

            var state = new GhostPlaybackState { vesselName = "ServoMirroredPoseProbe", ghost = result.root };
            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            RoboticGhostInfo plain = null, mirrored = null;
            InGameAssert.IsTrue(state.roboticInfos != null
                && state.roboticInfos.TryGetValue(FlightRecorder.EncodeEngineKey(pair.plainPid, 0), out plain)
                && state.roboticInfos.TryGetValue(FlightRecorder.EncodeEngineKey(pair.mirroredPid, 0), out mirrored)
                && plain?.servoTransform != null && mirrored?.servoTransform != null,
                $"both halves of '{pair.partName}' (pids {pair.plainPid} / {pair.mirroredPid}) should "
                + "expose a servo transform on the ghost");
            InGameAssert.IsTrue(plain.visualMode == RoboticVisualMode.Rotational
                && mirrored.visualMode == RoboticVisualMode.Rotational,
                "both halves should pose as Rotational servos");
            InGameAssert.IsTrue(plain.hasSnapshotBaseline && mirrored.hasSnapshotBaseline,
                "both halves should have taken their snapshot targetAngle as the spawn baseline");

            // ---- sample 1: the spawn (the snapshot baseline, the fix) ----
            float spawnPlain = DisplayedAngle(plain);
            float spawnMirrored = DisplayedAngle(mirrored);
            InGameAssert.IsGreaterThan(Mathf.Abs(spawnPlain), MinPosedDegrees,
                $"the pair must start posed for the mirror check to mean anything (plain half "
                + $"spawned at {F(spawnPlain)} deg, snapshot targetAngle {F(pair.plainTarget)})");
            InGameAssert.IsLessThan(Mathf.Abs(Mathf.DeltaAngle(spawnPlain, pair.plainTarget)),
                SpawnMirrorToleranceDegrees,
                $"the unmirrored half should spawn at its targetAngle {F(pair.plainTarget)}, got {F(spawnPlain)}");
            InGameAssert.IsLessThan(MirrorResidualDegrees(spawnPlain, spawnMirrored), SpawnMirrorToleranceDegrees,
                $"the halves should spawn as mirror images: plain {F(spawnPlain)} deg, mirrored "
                + $"{F(spawnMirrored)} deg (mirrored targetAngle {F(pair.mirroredTarget)})");

            InGameAssert.IsTrue(plain.hasSnapshotGeometryPose && mirrored.hasSnapshotGeometryPose,
                "the snapshot should carry stock's servoTransformRotation for both halves");
            float stockOffPlain = Quaternion.Angle(plain.servoTransform.localRotation, plain.snapshotGeometryRot);
            float stockOffMirrored = Quaternion.Angle(mirrored.servoTransform.localRotation, mirrored.snapshotGeometryRot);
            InGameAssert.IsLessThan(stockOffPlain, StockPoseToleranceDegrees,
                $"the unmirrored half should spawn at stock's persisted pose, off by {F(stockOffPlain)} deg");
            InGameAssert.IsLessThan(stockOffMirrored, StockPoseToleranceDegrees,
                $"the mirrored half should spawn at stock's persisted pose, off by {F(stockOffMirrored)} deg");

            int otherServos = 0, otherAtStock = 0;
            float worstOther = 0f;
            string worstOtherName = null;
            foreach (KeyValuePair<ulong, RoboticGhostInfo> kvp in state.roboticInfos)
            {
                RoboticGhostInfo info = kvp.Value;
                if (info == null || info == plain || info == mirrored || info.servoTransform == null
                    || info.visualMode != RoboticVisualMode.Rotational || !info.hasSnapshotGeometryPose)
                    continue;
                otherServos++;
                float off = Quaternion.Angle(info.servoTransform.localRotation, info.snapshotGeometryRot);
                if (off < StockPoseToleranceDegrees) otherAtStock++;
                if (off >= worstOther)
                {
                    worstOther = off;
                    worstOtherName = $"pid={info.partPersistentId} module={info.moduleName} "
                        + $"offset={F(info.poseAngleOffsetDegrees)}";
                }
            }
            InGameAssert.AreEqual(otherServos, otherAtStock,
                $"every other posed servo on the ghost should spawn at stock's persisted pose "
                + $"(worst {worstOtherName ?? "none"} off by {F(worstOther)} deg)");

            // ---- sample 2: the pair's recorded swing replayed to its last settled stop ----
            if (!TryResolveSettledSampleUT(rec.PartEvents, pair.plainPid, pair.mirroredPid,
                    out double sampleUT, out float lastPlain, out float lastMirrored))
            {
                InGameAssert.Fail($"recording {rec.RecordingId} carries no settled robotic stop for both "
                    + "halves of the pair; the lane's swing did not record");
                return;
            }
            int applied = 0;
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                PartEvent e = rec.PartEvents[i];
                if (e.moduleIndex != 0 || !IsRoboticPoseEvent(e.eventType) || e.ut > sampleUT) continue;
                if (e.partPersistentId != pair.plainPid && e.partPersistentId != pair.mirroredPid) continue;
                GhostPlaybackLogic.ApplyRoboticEvent(state, e, e.ut);
                applied++;
            }
            float sampledPlain = DisplayedAngle(plain);
            float sampledMirrored = DisplayedAngle(mirrored);
            InGameAssert.IsLessThan(Mathf.Abs(Mathf.DeltaAngle(sampledPlain, lastPlain)), RecordedValueToleranceDegrees,
                $"the unmirrored half should sit at its last recorded value {F(lastPlain)}, got {F(sampledPlain)}");
            InGameAssert.IsLessThan(Mathf.Abs(Mathf.DeltaAngle(sampledMirrored, lastMirrored)), RecordedValueToleranceDegrees,
                $"the mirrored half should sit at its last recorded value {F(lastMirrored)}, got {F(sampledMirrored)}");
            InGameAssert.IsLessThan(MirrorResidualDegrees(sampledPlain, sampledMirrored), SampleMirrorToleranceDegrees,
                $"the halves should stay mirror images at UT {sampleUT.ToString("F2", ic)}: plain "
                + $"{F(sampledPlain)} deg, mirrored {F(sampledMirrored)} deg");

            ParsekLog.Info("TestRunner",
                $"ServoMirroredPose: rec={rec.RecordingId} '{pair.partName}' plainPid={pair.plainPid} "
                + $"mirroredPid={pair.mirroredPid} linkedBySym={(pair.linkedBySym ? "true" : "false")} "
                + $"spawn plain={F(spawnPlain)} mirrored={F(spawnMirrored)} "
                + $"stockOff={F(stockOffPlain)}/{F(stockOffMirrored)} "
                + $"otherServosAtStock={otherAtStock.ToString(ic)}/{otherServos.ToString(ic)} "
                + $"sampleUT={sampleUT.ToString("F2", ic)} applied={applied.ToString(ic)} "
                + $"plain={F(sampledPlain)} mirrored={F(sampledMirrored)} mirror images");
        }
    }
}
