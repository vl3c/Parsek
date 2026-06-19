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
    /// scenario's GameEvent handlers are wired during <c>ParsekScenario.OnLoad</c>, a static
    /// handler throws on subscription; if the throw is swallowed (as the old
    /// <c>RegisterMainMenuHook</c> try/catch did) the hook silently never fires, and if it
    /// is not swallowed it aborts OnLoad. Either way the handler is dead.
    /// </summary>
    public class ScenarioGameEventHandlerContractTests
    {
        private static MethodInfo GetScenarioHandler(string name)
        {
            return typeof(Parsek.ParsekScenario).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.NonPublic | BindingFlags.Public);
        }

        [Fact]
        public void OnMainMenuTransition_MustBeInstanceMethod_NotStatic()
        {
            MethodInfo handler = GetScenarioHandler("OnMainMenuTransition");

            Assert.True(handler != null, "OnMainMenuTransition not found on ParsekScenario");
            Assert.False(
                handler.IsStatic,
                "OnMainMenuTransition must be an INSTANCE method. It is subscribed to "
                + "GameEvents.onGameSceneLoadRequested from RegisterMainMenuHook during "
                + "ParsekScenario.OnLoad; KSP's EventData.Add dereferences evt.Target (null for "
                + "a static handler) and throws NullReferenceException, so a static handler never "
                + "registers and the main-menu session reset (initialLoadDone / pending-cleanup / "
                + "PlaybackScopeTracker) plus the per-scene-switch recovery-funds flush go dead.");
        }
    }
}
