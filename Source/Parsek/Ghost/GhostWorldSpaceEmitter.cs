using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Leaves a ghost's world-space smoke behind in the world, as stock does a real part's. Unity's
    /// world is not the game's world: while the active vessel flies, Krakensbane carries the frame
    /// along with it and <c>FloatingOrigin.setOffset</c> moves a part's world-space particles back
    /// by the offset, each physics step. Stock finds them through the Part (its EFFECTS behaviours,
    /// its FXGroups) or the <c>FXPrefab</c> on the system; a ghost has no Part and its clones'
    /// <c>FXPrefab</c> is stripped, so this component is the one thing that moves a ghost's, and
    /// smoke without it rides along with the active vessel.
    ///
    /// Enabled means registered: Unity calls OnDisable on deactivation, destruction and scene
    /// unload, so nothing outside this class tracks the lifetime.
    /// </summary>
    internal sealed class GhostWorldSpaceEmitter : MonoBehaviour
    {
        private static readonly List<GhostWorldSpaceEmitter> registered = new List<GhostWorldSpaceEmitter>();

        /// <summary>Sum of every origin shift seen: compares positions read on two sides of a shift.</summary>
        private static Vector3d totalShift;

        /// <summary>Set for an emitter-fed system (stock creates its <c>ps</c> in Awake).</summary>
        internal KSPParticleEmitter emitter;

        /// <summary>Set for a plain particle system (a legacy <c>fx_*</c> clone, a PREFAB_PARTICLE).</summary>
        internal ParticleSystem system;

        private bool drifts;
        private static bool? plainSystemsDrift;

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

        /// <summary>
        /// Adds the component to every world-space system of a ghost FX clone. Stock drifts an
        /// emitter-fed system's particles besides shifting them; its own loops for the plain ones
        /// drop what they compute (the particle is a copy that is never written back), and
        /// KSPCommunityFixes' replacement of <c>setOffset</c> writes it. Returns the systems added.
        /// </summary>
        internal static int AttachAll(GameObject fxClone)
        {
            int added = 0;
            foreach (KSPParticleEmitter emitter in fxClone.GetComponentsInChildren<KSPParticleEmitter>(true))
            {
                if (!emitter.useWorldSpace) continue;
                GhostWorldSpaceEmitter e = emitter.gameObject.AddComponent<GhostWorldSpaceEmitter>();
                e.emitter = emitter;
                e.drifts = true;
                added++;
            }
            foreach (ParticleSystem ps in fxClone.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps.main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
                if (ps.GetComponent<KSPParticleEmitter>() != null) continue;
                GhostWorldSpaceEmitter e = ps.gameObject.AddComponent<GhostWorldSpaceEmitter>();
                e.system = ps;
                e.drifts = PlainSystemsDrift();
                added++;
            }
            if (added > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"world-space-fx-{fxClone.name}",
                    $"World-space FX follow the floating origin: fx='{fxClone.name}' systems={added} " +
                    $"registered={registered.Count} plainDrift={plainSystemsDrift}", 60.0);
            return added;
        }

        private static bool PlainSystemsDrift()
        {
            if (!plainSystemsDrift.HasValue)
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(AccessTools.Method(typeof(FloatingOrigin), "setOffset"));
                plainSystemsDrift = info != null && OwnersReplaceSetOffset(info.Owners);
            }
            return plainSystemsDrift.Value;
        }

        /// <summary>Whether KSPCommunityFixes is among the patch owners of <c>FloatingOrigin.setOffset</c>.</summary>
        internal static bool OwnersReplaceSetOffset(IEnumerable<string> owners)
        {
            foreach (string owner in owners)
                if (owner == "KSPCommunityFixes") return true;
            return false;
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
            if (transform.parent == null)
            {
                // Detached to fade out after its ghost went (DetachAndLingerParticleSystems): it
                // stands still in Unity space, so its "motion" would be the active vessel's.
                driftVelocity = Vector3.zero;
                return;
            }
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
        /// One particle's place after an origin shift: stock's rule
        /// (<c>EffectBehaviour.OffsetParticles</c>), which also drifts each particle back along the
        /// emitter's motion by a random part of one physics step.
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
            ParticleSystem ps = e.emitter != null ? e.emitter.ps : e.system;
            if (ps == null) return;
            int count = ps.particleCount;
            if (count == 0) return;
            Vector3 driftStep = e.drifts ? e.driftVelocity * Time.deltaTime : Vector3.zero;
            ParticleSystem.Particle[] buffer = ps.GetParticleBuffer();
            for (int i = count - 1; i >= 0; i--)
                buffer[i].position = ShiftParticle(buffer[i].position, shift, driftStep, Random.value);
            ps.SetParticles(buffer, count);
        }
    }
}
