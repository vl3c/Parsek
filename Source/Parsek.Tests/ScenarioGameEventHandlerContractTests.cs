using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// The PRIMARY guard (<see cref="AllScenarioGameEventHandlers_AreInstanceMethods_NotStatic"/>)
    /// SOURCE-SCANS ParsekScenario.cs for every <c>GameEvents.*.Add(/Remove(</c> subscription and
    /// asserts each resolved handler is non-static, so a FUTURE subscription is covered
    /// automatically — closing the gap that a hardcoded allowlist (the original form of this
    /// test) leaves open (the exact omission that cost the recording-index wipe). The
    /// <see cref="KnownScenarioGameEventHandler_IsInstanceMethod_NotStatic"/> Theory is a
    /// belt-and-suspenders enumeration of the handlers known to be wired during OnLoad.
    /// </summary>
    public class ScenarioGameEventHandlerContractTests
    {
        // GameEvents.<event>[.<sub>].Add( Handler )  or  .Remove( Handler ) — captures a bare
        // method-group identifier. Lambdas / new EventData<>.OnEvent(...) / instance-field
        // delegates are safe (non-null Target) and are not captured as bare identifiers.
        private static readonly Regex SubscriptionRx = new Regex(
            @"GameEvents\.[A-Za-z0-9_.]+\.(?:Add|Remove)\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)",
            RegexOptions.Compiled);

        [Fact]
        public void AllScenarioGameEventHandlers_AreInstanceMethods_NotStatic()
        {
            string source = ReadParsekScenarioSource();

            var handlerNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in SubscriptionRx.Matches(source))
                handlerNames.Add(m.Groups[1].Value);

            Assert.True(handlerNames.Count > 0,
                "Source scan found no GameEvents.*.Add(handler) subscriptions in ParsekScenario.cs — "
                + "the scan regex likely needs updating to match the current subscription idiom.");

            var offenders = new List<string>();
            foreach (string name in handlerNames)
            {
                MethodInfo handler = typeof(Parsek.ParsekScenario).GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.NonPublic | BindingFlags.Public);
                // A captured identifier that is not a ParsekScenario method (e.g. a local
                // delegate variable passed to Add) is out of scope — only assert on real
                // handler methods declared on ParsekScenario.
                if (handler == null) continue;
                if (handler.IsStatic) offenders.Add(name);
            }

            Assert.True(offenders.Count == 0,
                "These ParsekScenario GameEvent handlers are STATIC and will throw "
                + "NullReferenceException in KSP's EventData.Add (the EvtDelegate ctor dereferences "
                + "evt.Target, which is null for a static handler), aborting OnLoad / wiping the "
                + "recording index on the next save: [" + string.Join(", ", offenders)
                + "]. Make each an instance method (see the class summary).");
        }

        [Theory]
        [InlineData("OnMainMenuTransition")]          // GameEvents.onGameSceneLoadRequested
        [InlineData("OnGameSceneSwitchClearEscrow")]  // GameEvents.onGameSceneSwitchRequested
        [InlineData("OnVesselRecoveryProcessing")]    // GameEvents.onVesselRecoveryProcessing
        [InlineData("OnVesselRecovered")]             // GameEvents.onVesselRecovered
        [InlineData("OnVesselTerminated")]            // GameEvents.onVesselTerminated
        [InlineData("OnVesselSwitching")]             // GameEvents.onVesselSwitching
        public void KnownScenarioGameEventHandler_IsInstanceMethod_NotStatic(string handlerName)
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

        /// <summary>
        /// Locates Source/Parsek/ParsekScenario.cs by walking up from the test bin directory
        /// (xUnit runs from Source/Parsek.Tests/bin/Debug/net472/). Mirrors the source-gate
        /// pattern used by the ERS/ELS grep-audit and chain-state tests.
        /// </summary>
        private static string ReadParsekScenarioSource()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekScenario.cs");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException(
                "Could not locate Source/Parsek/ParsekScenario.cs by walking up from "
                + AppContext.BaseDirectory);
        }
    }
}
