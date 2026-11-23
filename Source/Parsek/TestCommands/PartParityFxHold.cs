using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The test stand of one FX case (design-part-ghost-parity.md E.2, the E0 hold): after every
    /// physics step (a <c>WaitForFixedUpdate</c> loop, which resumes after the simulation) the
    /// case vessel's root position, rotation, velocity and angular velocity are written back to
    /// its pose relative to the host, so neither thrust nor torque moves it, its internal and
    /// skin temperatures are written back to their spawn values (or to a heat case's target) and
    /// its blackbody glow is suppressed. The drift at RENDER time (read in <c>LateUpdate</c>, after
    /// the write) is the gate; the displacement one step makes before the write
    /// (<see cref="MaxStepM"/>, a dt^2) is logged, never gated: nothing renders in between.
    /// One per case; destroyed with the case.
    /// </summary>
    internal sealed class PartParityFxHold : MonoBehaviour
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        private Vessel real;
        private Vessel host;
        private Vector3 offsetLocal;
        private Quaternion rotationLocal;
        private bool armed;

        /// <summary>The spawn temperatures written back every step (K).</summary>
        private double spawnTemperature = double.NaN;
        private double spawnSkinTemperature = double.NaN;

        /// <summary>A heat case's target temperature (K), written instead of the spawn value;
        /// null for every other case.</summary>
        internal double? HeatTargetK;

        /// <summary>The heat module reads <c>skinTemperature</c> (its <c>useSkinTemp</c>).</summary>
        internal bool HeatUsesSkin;

        // ----- the evidence (fx.hold) -----

        internal double MaxRenderDriftM;
        internal double MaxRenderDriftDeg;
        internal double MaxStepM;
        internal int Steps;

        internal static PartParityFxHold Attach(Vessel real, Vessel host)
        {
            var go = new GameObject("PartParityFxHold");
            var h = go.AddComponent<PartParityFxHold>();
            h.Arm(real, host);
            return h;
        }

        /// <summary>Records the pose relative to the host's root and the temperatures; a later
        /// call re-arms on the current pose. Clears the evidence counters.</summary>
        internal void Arm(Vessel realVessel, Vessel hostVessel)
        {
            real = realVessel;
            host = hostVessel;
            Rigidbody hr = host.rootPart.Rigidbody, rr = real.rootPart.Rigidbody;
            Vector3 hp = hr != null ? hr.position : host.rootPart.transform.position;
            Quaternion hq = hr != null ? hr.rotation : host.rootPart.transform.rotation;
            Vector3 rp = rr != null ? rr.position : real.rootPart.transform.position;
            Quaternion rq = rr != null ? rr.rotation : real.rootPart.transform.rotation;
            offsetLocal = Quaternion.Inverse(hq) * (rp - hp);
            rotationLocal = Quaternion.Inverse(hq) * rq;
            spawnTemperature = real.rootPart.temperature;
            spawnSkinTemperature = real.rootPart.skinTemperature;
            armed = true;
            ResetEvidence();
        }

        /// <summary>Starts the drift evidence over (at the press, so the settle is not counted).</summary>
        internal void ResetEvidence()
        {
            MaxRenderDriftM = 0.0;
            MaxRenderDriftDeg = 0.0;
            MaxStepM = 0.0;
            Steps = 0;
        }

        private IEnumerator Start()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                WriteBack();
            }
        }

        private bool TargetPose(out Vector3 position, out Quaternion rotation, out Rigidbody hostBody, out Rigidbody realBody)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            hostBody = null;
            realBody = null;
            if (!armed || real == null || host == null || real.rootPart == null || host.rootPart == null) return false;
            hostBody = host.rootPart.Rigidbody;
            realBody = real.rootPart.Rigidbody;
            Vector3 hp = hostBody != null ? hostBody.position : host.rootPart.transform.position;
            Quaternion hq = hostBody != null ? hostBody.rotation : host.rootPart.transform.rotation;
            position = hp + hq * offsetLocal;
            rotation = hq * rotationLocal;
            return true;
        }

        private void WriteBack()
        {
            if (!TargetPose(out Vector3 target, out Quaternion targetRot, out Rigidbody hr, out Rigidbody rr)) return;
            Part p = real.rootPart;
            Vector3 current = rr != null ? rr.position : p.transform.position;
            double step = (current - target).magnitude;
            Steps++;
            if (step > MaxStepM) MaxStepM = step;
            if (rr != null)
            {
                rr.position = target;
                rr.rotation = targetRot;
                rr.velocity = hr != null ? hr.GetPointVelocity(target) : Vector3.zero;
                rr.angularVelocity = hr != null ? hr.angularVelocity : Vector3.zero;
            }
            p.transform.SetPositionAndRotation(target, targetRot);
            if (!double.IsNaN(spawnTemperature))
            {
                p.temperature = spawnTemperature;
                p.skinTemperature = spawnSkinTemperature;
                if (HeatTargetK.HasValue)
                {
                    if (HeatUsesSkin) p.skinTemperature = HeatTargetK.Value;
                    else p.temperature = HeatTargetK.Value;
                }
            }
            p.blackBodyRadiationAlphaMult = 0f;
        }

        private void LateUpdate()
        {
            if (!TargetPose(out Vector3 target, out Quaternion targetRot, out _, out _)) return;
            Transform t = real.rootPart.transform;
            double d = (t.position - target).magnitude;
            if (d > MaxRenderDriftM) MaxRenderDriftM = d;
            double a = Quaternion.Angle(t.rotation, targetRot);
            if (a > MaxRenderDriftDeg) MaxRenderDriftDeg = a;
        }

        /// <summary>The per-case <c>hold drift</c> line's body.</summary>
        internal string Describe()
            => $"maxDriftM={MaxRenderDriftM.ToString("0.00000", Ic)} maxDriftDeg={MaxRenderDriftDeg.ToString("0.000", Ic)} " +
               $"stepM={MaxStepM.ToString("0.0000", Ic)} steps={Steps.ToString(Ic)} " +
               $"heatTargetK={(HeatTargetK.HasValue ? HeatTargetK.Value.ToString("0.0", Ic) : "none")} skin={(HeatUsesSkin ? "true" : "false")}";
    }
}
