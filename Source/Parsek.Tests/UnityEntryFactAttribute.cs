using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A <see cref="FactAttribute"/> for a test whose code under test has a body that calls a Unity
    /// native method directly (<c>GameObject.SetActive</c>, <c>activeSelf</c>,
    /// <c>Time.realtimeSinceStartup</c>), even when the test never takes that branch.
    ///
    /// The two headless hosts disagree about such a body. Mono enters it and refuses only the native
    /// call itself (MissingMethodException), so every guard before the call runs and the test sees the
    /// real behavior. The Windows .NET Framework test host refuses the whole method on ENTRY
    /// (<c>SecurityException: ECall methods must be packaged into a system module</c>), before its first
    /// statement (see GhostPartEventApplyLogTests). A characterization transcript recorded on mono then
    /// cannot match on Windows, so the test is skipped there: mono, which CI runs, stays the proof.
    ///
    /// The decision is a runtime probe, not a platform check: a NoInlining method that sets a flag and
    /// then calls <c>SetActive</c>. If the flag was never set, the host refused on entry. xUnit 2.4 has
    /// no runtime skip, so the probe runs at discovery, as the other skip attributes here do.
    /// </summary>
    internal sealed class UnityEntryFactAttribute : FactAttribute
    {
        internal const string SkipReason =
            "This host refuses a method that calls a Unity native method on entry (the Windows .NET "
            + "Framework test host); the code under test has such a body, so the mono run (CI) carries "
            + "this test";

        private static readonly Lazy<bool> RefusesOnEntry = new Lazy<bool>(ProbeRefusesOnEntry);

        public UnityEntryFactAttribute()
        {
            if (RefusesOnEntry.Value)
                Skip = SkipReason;
        }

        internal static bool HostRefusesUnityBodiesOnEntry => RefusesOnEntry.Value;

        private static bool entered;

        private static bool ProbeRefusesOnEntry()
        {
            entered = false;
            try
            {
                TouchUnityNative(null);
            }
            catch (Exception)
            {
                // Mono refuses the native call itself (or the null receiver): expected either way.
            }
            return !entered;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TouchUnityNative(GameObject go)
        {
            entered = true;
            go.SetActive(false);
        }
    }
}

namespace Parsek.Tests
{
    public class UnityEntryFactAttributeTests
    {
        [Fact]
        public void Probe_OnMono_EntersTheBody_SoTheProofIsNeverSkippedThere()
        {
            // Mono (the CI host) must run every UnityEntryFact: a probe that misread mono as refusing
            // on entry would silently skip the whole proof there.
            // Off mono either answer is legal, so the cell asserts nothing there.
            if (Type.GetType("Mono.Runtime") != null)
                Assert.False(UnityEntryFactAttribute.HostRefusesUnityBodiesOnEntry);
        }
    }
}
