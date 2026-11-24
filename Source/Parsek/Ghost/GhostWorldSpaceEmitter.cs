using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Leaves a ghost's world-space <c>KSPParticleEmitter</c> smoke behind in the world, as stock
    /// does a real part's. Unity's world is not the game's world: while the active vessel flies,
    /// Krakensbane carries the frame along with it and <c>FloatingOrigin.setOffset</c> moves every
    /// registered world-space particle back by the offset, each physics step
    /// (<c>EffectBehaviour.OffsetParticles</c>). Stock registers an emitter from the part's
    /// <c>ModelMultiParticleFX</c> / <c>ModelParticleFX</c>, which a ghost does not have, and an
    /// unregistered emitter's smoke rides along with the active vessel.
    ///
    /// Enabled means registered: Unity calls OnDisable on deactivation, destruction and scene
    /// unload, so nothing outside this class tracks the lifetime. A system that carries stock's
    /// <c>FXPrefab</c> registers itself and gets no component.
    /// </summary>
    internal sealed class GhostWorldSpaceEmitter : MonoBehaviour
    {
        private static readonly List<GhostWorldSpaceEmitter> registered = new List<GhostWorldSpaceEmitter>();

        /// <summary>Sum of every origin shift seen: compares positions read on two sides of a shift.</summary>
        private static Vector3d totalShift;

        internal KSPParticleEmitter emitter;

        private bool hasSample;
        private Vector3 lastPosition;
        private Vector3d lastTotalShift;
        private double lastUT;
        private Vector3 lastVelocity;
        private Vector3 driftVelocity;

        internal static int RegisteredCount => registered.Count;

        internal static Vector3d TotalShiftForTesting => totalShift;

        internal static void ResetForTesting()
        {
            registered.Clear();
            totalShift = Vector3d.zero;
        }

        private void OnEnable()
        {
            hasSample = false;
            lastVelocity = Vector3.zero;
            driftVelocity = Vector3.zero;
            registered.Add(this);
        }

        private void OnDisable()
        {
            registered.Remove(this);
        }

        /// <summary>
        /// The emitter's velocity in the physics frame, read off its own motion (a ghost has no
        /// rigidbody). Ghosts move when the clock does, so a frame in which it stood still is not
        /// a sample.
        /// </summary>
        private void LateUpdate()
        {
            double ut = Planetarium.GetUniversalTime();
            if (hasSample && ut == lastUT) return;
            Vector3 position = transform.position;
            if (hasSample)
            {
                Vector3 velocity = WorldVelocity(position - lastPosition, totalShift - lastTotalShift, ut - lastUT);
                driftVelocity = Slower(lastVelocity, velocity);
                lastVelocity = velocity;
            }
            hasSample = true;
            lastPosition = position;
            lastTotalShift = totalShift;
            lastUT = ut;
        }

        /// <summary>
        /// Velocity from a transform's move over <paramref name="seconds"/> of game time and the
        /// origin shifts applied in between (a shift moves the world by its negative). Zero for a
        /// clock that went back or stood still (a rewind, a load).
        /// </summary>
        internal static Vector3 WorldVelocity(Vector3 moved, Vector3d shifted, double seconds)
        {
            if (!(seconds > 0.0) || double.IsInfinity(seconds)) return Vector3.zero;
            return new Vector3(
                (float)((moved.x + shifted.x) / seconds),
                (float)((moved.y + shifted.y) / seconds),
                (float)((moved.z + shifted.z) / seconds));
        }

        /// <summary>
        /// The slower of two successive readings: a jump of the ghost (a loop restart, a rebuilt
        /// anchor) reads as one huge velocity between two ordinary ones and must not smear the
        /// smoke along the jump.
        /// </summary>
        internal static Vector3 Slower(Vector3 previous, Vector3 current)
            => previous.sqrMagnitude < current.sqrMagnitude ? previous : current;

        /// <summary>
        /// One particle's place after an origin shift: stock's rule for a
        /// <c>KSPParticleEmitter</c> (<c>EffectBehaviour.OffsetParticles</c>), which also drifts
        /// each particle back along the emitter's motion by a random part of one physics step.
        /// </summary>
        internal static Vector3 ShiftParticle(Vector3 position, Vector3d shift, Vector3 driftStep, float random)
        {
            return new Vector3(
                (float)(position.x - (double)driftStep.x * random - shift.x),
                (float)(position.y - (double)driftStep.y * random - shift.y),
                (float)(position.z - (double)driftStep.z * random - shift.z));
        }

        /// <summary>False for a shift stock's <c>setOffset</c> refuses before it moves anything.</summary>
        internal static bool IsUsableShift(Vector3d shift)
        {
            double sum = shift.x + shift.y + shift.z;
            return !double.IsNaN(sum) && !double.IsInfinity(sum);
        }

        /// <summary>
        /// After <c>FloatingOrigin.setOffset(refPos, nonFrame)</c>: moves the particles of every
        /// registered emitter as stock moved the real ones. Runs once a physics step while the
        /// active vessel flies and never while it is parked; uses stock's shared particle buffer,
        /// so nothing is allocated.
        /// </summary>
        internal static void OnOriginShifted(Vector3d refPos, Vector3d nonFrame)
        {
            Vector3d shift = refPos + nonFrame;
            if (!IsUsableShift(shift)) return;
            totalShift += shift;
            for (int i = registered.Count - 1; i >= 0; i--)
                MoveParticles(registered[i], shift);
        }

        // Particle ECalls; NoInlining keeps them out of OnOriginShifted, which a headless test may
        // call with nothing registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MoveParticles(GhostWorldSpaceEmitter e, Vector3d shift)
        {
            KSPParticleEmitter source = e.emitter;
            if (source == null || !source.useWorldSpace || source.ps == null) return;
            int count = source.ps.particleCount;
            if (count == 0) return;
            Vector3 driftStep = e.driftVelocity * Time.deltaTime;
            ParticleSystem.Particle[] buffer = source.ps.GetParticleBuffer();
            for (int i = count - 1; i >= 0; i--)
                buffer[i].position = ShiftParticle(buffer[i].position, shift, driftStep, Random.value);
            source.ps.SetParticles(buffer, count);
        }
    }
}
