using System;

namespace Parsek
{
    /// <summary>
    /// Process-wide identity helper for Parsek. Hosts a single
    /// <see cref="ProcessSessionId"/> GUID that identifies the current
    /// game process / AppDomain. The GUID is generated once per
    /// AppDomain when this type is first accessed and stays constant
    /// for the lifetime of the running KSP instance.
    ///
    /// <para>This is deliberately NOT a <see cref="UnityEngine.MonoBehaviour"/>
    /// and NOT initialized from <c>OnAwake</c>: Unity reinstantiates
    /// MonoBehaviour-derived objects (including <see cref="ParsekScenario"/>)
    /// on every scene transition, which would regenerate the GUID and
    /// defeat the cross-scene-marker freshness mechanism described in
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>.
    /// A static-class field initializer runs once per AppDomain on
    /// first type access — that is the correct lifetime.</para>
    ///
    /// <para>Used by stock-action intent markers (Tracking Station Fly,
    /// KSC marker Fly, Map view "Switch To") to deterministically detect
    /// cross-run-orphaned serialized markers: marker captures the value
    /// at arm time, the OnLoad tail compares to the current value and
    /// clears with <c>stale-cross-run</c> on mismatch.</para>
    /// </summary>
    internal static class ParsekProcess
    {
        private static Guid s_processSessionId = Guid.NewGuid();

        /// <summary>
        /// GUID identifying this AppDomain / game process. Stable for the
        /// lifetime of the running game; regenerated only on a fresh
        /// process start (and by <see cref="ResetForTesting"/> in tests).
        /// </summary>
        internal static Guid ProcessSessionId => s_processSessionId;

        /// <summary>
        /// Regenerate <see cref="ProcessSessionId"/>. Tests touching
        /// cross-run-orphan logic call this between test cases so each
        /// test sees a clean "fresh process" identity. Production code
        /// MUST NOT call this.
        /// </summary>
        internal static void ResetForTesting()
        {
            s_processSessionId = Guid.NewGuid();
        }
    }
}
