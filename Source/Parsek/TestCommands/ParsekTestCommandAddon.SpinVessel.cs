using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the automation-only <c>SpinVessel</c> verb; the contract is
    /// on <see cref="TestCommandSpinVessel"/>. SINGLE-PHASE: the spin is applied inside the
    /// call and physics carries it from the next FixedUpdate.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void SpinVesselImpl(ParsedCommand cmd)
        {
            string rateArg = ArgOrNull(cmd, TestCommandSpinVessel.RateKey);
            string reason = TestCommandSpinVessel.ValidateRate(rateArg, out float rate);
            if (reason != null)
            {
                RejectSpinVessel(reason, $"rate={rateArg ?? string.Empty}");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                RejectSpinVessel("spinvessel-no-active-vessel", "no active vessel");
                return;
            }
            if (v.packed || !v.loaded)
            {
                RejectSpinVessel("spinvessel-vessel-packed",
                    $"vessel={v.vesselName} packed={Bool(v.packed)} loaded={Bool(v.loaded)}");
                return;
            }

            // SAS would damp the spin straight back out (and PersistentRotation reads the SAS
            // group to pick its stability mode), so the verb owns turning it off.
            bool sasWasOn = v.ActionGroups[KSPActionGroup.SAS];
            if (sasWasOn)
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);

            Transform reference = v.ReferenceTransform != null ? v.ReferenceTransform : v.transform;
            Vector3 omega = TestCommandSpinVessel.ComputeRollAngularVelocityWorld(reference.rotation, rate);
            Vector3 com = v.CoM;
            int applied = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                Rigidbody rb = p != null ? p.rb : null;
                if (rb == null)
                    continue;
                rb.angularVelocity = omega;
                rb.velocity += TestCommandSpinVessel.ComputeTangentialVelocity(
                    omega, rb.worldCenterOfMass, com);
                applied++;
            }
            if (applied == 0)
            {
                RejectSpinVessel("spinvessel-no-rigidbodies", $"vessel={v.vesselName} parts={Int(v.parts.Count)}");
                return;
            }

            ParsekLog.Info(Tag, TestCommandSpinVessel.FormatAppliedLine(
                v.vesselName, v.persistentId, rate, applied, sasWasOn));
            SetExecResult("OK", Payload(
                Kv("spun", "true"),
                Kv("parts", Int(applied)),
                Kv("sasWasOn", Bool(sasWasOn))), null);
        }

        private void RejectSpinVessel(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"spinvessel rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
