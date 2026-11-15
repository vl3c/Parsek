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

        private static bool s_applicationQuitting;

        /// <summary>
        /// True once Unity has started quitting the process (set from
        /// <c>ParsekHarmony.OnApplicationQuit</c>, which Unity calls on every live
        /// MonoBehaviour BEFORE it destroys the scene's objects). Never cleared in
        /// production: a quit is not cancellable in KSP, and every object destroyed after
        /// this point dies with the process. Teardown-time repair paths read it to stand
        /// down instead of rebuilding stock objects that are already being destroyed.
        /// </summary>
        internal static bool IsApplicationQuitting => s_applicationQuitting;

        /// <summary>
        /// Latch <see cref="IsApplicationQuitting"/>. Idempotent; logs the first latch only.
        /// </summary>
        internal static void MarkApplicationQuitting(string source)
        {
            if (s_applicationQuitting)
                return;
            s_applicationQuitting = true;
            ParsekLog.Info("Init",
                "Application quitting latched source=" + (source ?? "(none)") +
                " - teardown-time repair paths stand down from here");
        }

        /// <summary>Clear the quitting latch. Tests only.</summary>
        internal static void ResetApplicationQuittingForTesting()
        {
            s_applicationQuitting = false;
        }
    }
}
