using System;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure route dispatch for an accepted <c>SetSetting</c> (P5.1 / C3). The
    /// <see cref="SettingWhitelist"/> already produced the typed value and the
    /// <see cref="PersistenceRoute"/>; this applier decides which side effects the
    /// thin Unity applier must run:
    /// <list type="bullet">
    /// <item><description>ALWAYS set the live <c>ParsekSettings.Current</c> field
    /// (<paramref name="setLiveField"/>).</description></item>
    /// <item><description>For a <see cref="PersistenceRoute.GameParametersPlusSidecar"/>
    /// setting ALSO invoke the exact <c>ParsekSettingsPersistence.Record*</c> member
    /// (<paramref name="invokeRecordMethod"/>), so the value survives the next
    /// <c>ParsekScenario.OnLoad</c> <c>ApplyTo</c> (which OVERWRITES the GameParameters
    /// value at every save load, including a <c>LoadGame</c>-driven load).</description></item>
    /// </list>
    /// The two side effects are passed as delegates so the routing decision is
    /// exercised by xUnit with spies (asserting the route enum + that the Record*
    /// path fires for the 8 tracked names and NOT for the 8 GameParameters-only
    /// names) without a live Unity <c>ParsekSettings</c> (which derives from the
    /// Assembly-CSharp <c>GameParameters.CustomParameterNode</c> and cannot be
    /// constructed headless).
    /// </summary>
    internal static class TestCommandSettingApplier
    {
        /// <summary>
        /// Applies an accepted whitelist decision. Precondition:
        /// <c>result.Accepted == true</c> (the caller rejects before calling this).
        /// Sets the live field unconditionally; invokes the Record* selector only for
        /// a sidecar-tracked setting.
        /// </summary>
        internal static void ApplySetting(
            SettingApplyResult result,
            Action<SettingApplyResult> setLiveField,
            Action<SettingApplyResult> invokeRecordMethod)
        {
            setLiveField(result);
            if (result.Route == PersistenceRoute.GameParametersPlusSidecar)
                invokeRecordMethod(result);
        }
    }
}
