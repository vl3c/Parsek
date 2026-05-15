using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Decides whether a background-tracked recording has lost its recorded
    /// controllable identity by the time the surviving vessel goes on rails.
    /// KSP sets <see cref="Vessel.Situations.LANDED"/> purely from terrain
    /// proximity, so a destructive breakup that leaves a tiny remnant (e.g. a
    /// 1-part decoupler) at non-trivial surface speed gets <c>situation=LANDED</c>
    /// for free. Without this check the BG cache refresh accepts that as a
    /// terminal "Landed" verdict, and the recording is permanently
    /// mis-classified (see <c>fix-bg-identity-loss-destroyed</c>).
    /// </summary>
    internal static class IdentityLossClassifier
    {
        /// <summary>
        /// Pure predicate, no Unity types. Returns <c>true</c> when the
        /// surviving remnant carries none of the recorded controller part PIDs
        /// AND is not classified by the live trackability contract as
        /// controllable in its own right (SpaceObject, EVA kerbal, or any
        /// surviving <c>ModuleCommand</c> part). Used by the BG go-on-rails
        /// seam to flip the recording to <see cref="TerminalState.Destroyed"/>
        /// before the FinalizerCache short-circuit can write a false Landed.
        ///
        /// <para>
        /// Forward-only on legacy: if a recording was captured before
        /// controller-identity tracking existed, <paramref name="recordedControllerPids"/>
        /// is empty/null and the predicate returns <c>false</c> — preserving
        /// today's behavior on pre-existing recordings. The fix is forward-only
        /// by design (see plan §"Forward-only on legacy `Controllers == null`").
        /// </para>
        /// </summary>
        /// <param name="isDebris">
        /// <see cref="Recording.IsDebris"/>: debris recordings opt out — they
        /// were never expected to carry controllable identity.
        /// </param>
        /// <param name="recordedControllerPids">
        /// Persistent IDs of the controller parts captured at recording-start
        /// time. <c>null</c> or empty disables the override.
        /// </param>
        /// <param name="liveIsTrackable">
        /// Result of <see cref="ParsekFlight.IsTrackableVessel(Vessel)"/>
        /// applied to the surviving vessel: <c>true</c> for SpaceObjects, EVA
        /// kerbals, or any vessel carrying at least one <see cref="ModuleCommand"/>
        /// part. When the live remnant is still trackable, the identity is
        /// preserved by the contract that already governs split classification
        /// elsewhere in the mod; the override does not fire.
        /// </param>
        /// <param name="livePartPids">
        /// Persistent IDs of the parts remaining on the surviving vessel.
        /// </param>
        internal static bool ShouldClassifyRecordedIdentityLost(
            bool isDebris,
            IReadOnlyList<uint> recordedControllerPids,
            bool liveIsTrackable,
            IReadOnlyList<uint> livePartPids)
        {
            if (isDebris) return false;
            if (recordedControllerPids == null || recordedControllerPids.Count == 0)
                return false;
            if (liveIsTrackable) return false;
            if (livePartPids == null || livePartPids.Count == 0)
                return true; // no parts survive at all → definitely no recorded identity

            for (int i = 0; i < recordedControllerPids.Count; i++)
            {
                uint cpid = recordedControllerPids[i];
                if (cpid == 0) continue;
                for (int j = 0; j < livePartPids.Count; j++)
                {
                    if (livePartPids[j] == cpid)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Live wrapper for the BG go-on-rails seam. Adapts the live KSP types
        /// to the pure predicate via <see cref="ParsekFlight.IsTrackableVessel"/>
        /// and a single pass over <c>v.parts</c>. Null-safe on both inputs.
        /// </summary>
        internal static bool IsRecordedIdentityLost(Recording rec, Vessel v)
        {
            if (rec == null || v == null)
                return false;

            List<uint> recordedPids = null;
            if (rec.Controllers != null && rec.Controllers.Count > 0)
            {
                recordedPids = new List<uint>(rec.Controllers.Count);
                for (int i = 0; i < rec.Controllers.Count; i++)
                    recordedPids.Add(rec.Controllers[i].partPersistentId);
            }

            List<uint> livePids = null;
            if (v.parts != null && v.parts.Count > 0)
            {
                livePids = new List<uint>(v.parts.Count);
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null) livePids.Add(p.persistentId);
                }
            }

            return ShouldClassifyRecordedIdentityLost(
                rec.IsDebris,
                recordedPids,
                ParsekFlight.IsTrackableVessel(v),
                livePids);
        }
    }
}
