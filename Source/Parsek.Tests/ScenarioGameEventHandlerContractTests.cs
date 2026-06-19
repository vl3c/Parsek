using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Contract guard: every method <see cref="Parsek.ParsekScenario"/> subscribes to a KSP
    /// <c>GameEvents</c> hook MUST be an instance method, never <c>static</c>.
    ///
    /// KSP's <c>EventData&lt;T&gt;.Add</c> wraps the handler in an <c>EvtDelegate</c> whose
    /// constructor unconditionally runs <c>originatorType = evt.Target.GetType().Name</c>
    /// (verified by decompiling Assembly-CSharp). A static method's delegate has a null
    /// <c>Target</c>, so <c>Add</c> throws <c>NullReferenceException</c>. Because the
    /// scenario's GameEvent handlers are wired from <c>SubscribeVesselLifecycleEvents</c>
    /// during <c>ParsekScenario.OnLoad</c>, a static handler aborts the entire OnLoad before
    /// recordings are loaded; the next save then writes 0 RECORDING_TREE nodes and silently
    /// wipes the recording index (the CleanOrphanFiles guard preserves the sidecars, but the
    /// recordings disappear from the UI). Regression guard for the logistics-m4
    /// <c>OnGameSceneSwitchClearEscrow</c> NRE.
    /// </summary>
    public class ScenarioGameEventHandlerContractTests
    {
        [Fact]
        public void OnGameSceneSwitchClearEscrow_MustBeInstanceMethod_NotStatic()
        {
            MethodInfo handler = typeof(Parsek.ParsekScenario).GetMethod(
                "OnGameSceneSwitchClearEscrow",
                BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.True(handler != null, "OnGameSceneSwitchClearEscrow not found on ParsekScenario");
            Assert.False(
                handler.IsStatic,
                "OnGameSceneSwitchClearEscrow must be an INSTANCE method. KSP's EventData.Add "
                + "dereferences evt.Target (null for a static handler) and throws NullReferenceException, "
                + "which aborts ParsekScenario.OnLoad and wipes the recording index on the next save.");
        }
    }
}
