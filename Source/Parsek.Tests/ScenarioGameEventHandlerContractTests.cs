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
    /// <c>Target</c>, so <c>Add</c> throws <c>NullReferenceException</c>. Because these handlers
    /// are wired during <c>ParsekScenario.OnLoad</c>, a static handler either aborts the entire
    /// OnLoad before recordings load — the <c>OnGameSceneSwitchClearEscrow</c> case, which wiped
    /// the recording index on the next save — or, if the throw is swallowed (the old
    /// <c>RegisterMainMenuHook</c> try/catch around <c>OnMainMenuTransition</c>), silently never
    /// registers so the hook is dead. Either way the handler is broken.
    ///
    /// When a new ParsekScenario GameEvent subscription is added, add its handler name to
    /// <see cref="ScenarioGameEventHandler_MustBeInstanceMethod_NotStatic"/> below.
    /// </summary>
    public class ScenarioGameEventHandlerContractTests
    {
        [Theory]
        [InlineData("OnMainMenuTransition")]          // GameEvents.onGameSceneLoadRequested
        [InlineData("OnGameSceneSwitchClearEscrow")]  // GameEvents.onGameSceneSwitchRequested
        [InlineData("OnVesselRecoveryProcessing")]    // GameEvents.onVesselRecoveryProcessing
        [InlineData("OnVesselRecovered")]             // GameEvents.onVesselRecovered
        [InlineData("OnVesselTerminated")]            // GameEvents.onVesselTerminated
        [InlineData("OnVesselSwitching")]             // GameEvents.onVesselSwitching
        public void ScenarioGameEventHandler_MustBeInstanceMethod_NotStatic(string handlerName)
        {
            MethodInfo handler = typeof(Parsek.ParsekScenario).GetMethod(
                handlerName,
                BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.True(handler != null, $"{handlerName} not found on ParsekScenario");
            Assert.False(
                handler.IsStatic,
                $"{handlerName} must be an INSTANCE method. KSP's EventData.Add dereferences "
                + "evt.Target (null for a static handler) in the EvtDelegate ctor and throws "
                + "NullReferenceException. Subscribed during ParsekScenario.OnLoad, a static handler "
                + "either aborts OnLoad (wiping the recording index on the next save) or, if the "
                + "throw is swallowed, silently never registers so the hook goes dead.");
        }
    }
}
