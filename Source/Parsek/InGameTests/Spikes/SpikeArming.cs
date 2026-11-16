using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan:
    // docs/dev/plans/multiplayer/r0-spikes.md, task S0.
    //
    // The env var PARSEK_SPIKE names the armed spikes as a comma list ("S4a,S9").
    // It is read once, at Startup.Instantly; a change needs a KSP restart. Unset or
    // empty: the host logs nothing, creates no Harmony instance and applies nothing,
    // so the spike DLL behaves as the multiplayer branch it was cut from.
    //
    // Spike patches must NOT carry [HarmonyPatch]: ParsekHarmony applies every such
    // type in the assembly in every session. A spike's patches are applied by hand
    // from SpikeRegistry.All, through the separate Harmony id below, only when that
    // spike's id is in PARSEK_SPIKE.

    /// <summary>Pure parts of the arming contract (xUnit-tested).</summary>
    internal static class SpikeArming
    {
        internal const string EnvVarName = "PARSEK_SPIKE";

        // Grepped in the deployed DLL as UTF-16 (the #US / constant blob) by the
        // spike procedure; keep it a plain literal.
        internal const string BuildMarker = "PARSEK-SPIKE-R0";

        internal const string HarmonyId = "com.parsek.spike";

        internal const string Tag = "Spike";

        private static readonly IReadOnlyList<string> Empty = new string[0];

        /// <summary>
        /// Comma list -> trimmed, non-empty ids, first spelling kept, duplicates
        /// (case-insensitive) dropped. Null, empty or only separators -> empty list.
        /// </summary>
        internal static IReadOnlyList<string> ParseList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Empty;
            var ids = new List<string>();
            foreach (string part in raw.Split(','))
            {
                string id = part.Trim();
                if (id.Length == 0) continue;
                if (Contains(ids, id)) continue;
                ids.Add(id);
            }
            return ids.Count == 0 ? Empty : ids.AsReadOnly();
        }

        /// <summary>True when <paramref name="spikeId"/> is in the armed list (case-insensitive).</summary>
        internal static bool IsArmed(IReadOnlyList<string> armed, string spikeId)
        {
            if (armed == null || string.IsNullOrEmpty(spikeId)) return false;
            return Contains(armed, spikeId.Trim());
        }

        /// <summary>The armed line's message body, after the [Parsek][INFO][Spike] prefix.</summary>
        internal static string FormatArmedMessage(IReadOnlyList<string> armed)
            => "armed: " + string.Join(",", armed ?? Empty) + " build=" + BuildMarker;

        /// <summary>Armed ids that no registration names (a typo in PARSEK_SPIKE).</summary>
        internal static List<string> UnknownIds(IReadOnlyList<string> armed, IEnumerable<string> registeredIds)
        {
            var known = new List<string>(registeredIds ?? new string[0]);
            var unknown = new List<string>();
            if (armed == null) return unknown;
            foreach (string id in armed)
                if (!Contains(known, id)) unknown.Add(id);
            return unknown;
        }

        /// <summary>Log tag of one spike: "Spike.S4a".</summary>
        internal static string SpikeTag(string spikeId)
            => string.IsNullOrEmpty(spikeId) ? Tag : Tag + "." + spikeId;

        private static bool Contains(IReadOnlyList<string> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>One spike's hand-applied patch set. <c>Apply</c> may be null (S0 has none).</summary>
    internal sealed class SpikeRegistration
    {
        internal readonly string Id;
        internal readonly Action<Harmony> Apply;

        internal SpikeRegistration(string id, Action<Harmony> apply)
        {
            Id = id;
            Apply = apply;
        }
    }

    /// <summary>
    /// The one list every spike adds its line to. An id armed in PARSEK_SPIKE that is
    /// not listed here is logged as unknown and does nothing.
    /// </summary>
    internal static class SpikeRegistry
    {
        internal static readonly SpikeRegistration[] All =
        {
            // S0: the arming proof itself; no patches.
            new SpikeRegistration("S0", null),
            // Each spike adds one line here, for example:
            // new SpikeRegistration("S4a", SpikeS4EventOrder.Apply),
        };

        internal static IEnumerable<string> Ids()
        {
            foreach (var r in All) yield return r.Id;
        }
    }

    /// <summary>
    /// Reads PARSEK_SPIKE once and applies the armed spikes' patches. Inert when unset.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SpikeHost : MonoBehaviour
    {
        private static SpikeHost instance;
        private static IReadOnlyList<string> armedIds = new string[0];
        private static Harmony harmony;

        /// <summary>The armed ids, read once at Awake. Empty when PARSEK_SPIKE is unset.</summary>
        internal static IReadOnlyList<string> ArmedIds => armedIds;

        /// <summary>The spike Harmony instance; null when nothing is armed.</summary>
        internal static Harmony SpikeHarmony => harmony;

        /// <summary>True when the spike id is armed in this process.</summary>
        internal static bool IsArmed(string spikeId) => SpikeArming.IsArmed(armedIds, spikeId);

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            armedIds = SpikeArming.ParseList(Environment.GetEnvironmentVariable(SpikeArming.EnvVarName));
            if (armedIds.Count == 0)
                return; // unarmed: no log line, no Harmony instance, no patch

            ParsekLog.Info(SpikeArming.Tag, SpikeArming.FormatArmedMessage(armedIds));

            foreach (string id in SpikeArming.UnknownIds(armedIds, SpikeRegistry.Ids()))
                ParsekLog.Warn(SpikeArming.Tag, "armed id has no registration: " + id);

            harmony = new Harmony(SpikeArming.HarmonyId);
            int applied = 0, failed = 0, noPatches = 0;
            foreach (SpikeRegistration reg in SpikeRegistry.All)
            {
                if (!SpikeArming.IsArmed(armedIds, reg.Id)) continue;
                if (reg.Apply == null)
                {
                    noPatches++;
                    ParsekLog.Info(SpikeArming.Tag, "spike " + reg.Id + ": no patches to apply");
                    continue;
                }
                try
                {
                    reg.Apply(harmony);
                    applied++;
                    ParsekLog.Info(SpikeArming.Tag, "spike " + reg.Id + ": patches applied (harmony=" + SpikeArming.HarmonyId + ")");
                }
                catch (Exception ex)
                {
                    failed++;
                    ParsekLog.Warn(SpikeArming.Tag, "spike " + reg.Id + ": patch apply failed: " + ex);
                }
            }
            ParsekLog.Info(SpikeArming.Tag, string.Format(CultureInfo.InvariantCulture,
                "patch summary: applied={0} failed={1} noPatches={2} root={3}",
                applied, failed, noPatches, SafeRoot()));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SafeRoot()
        {
            try { return SpikeCsv.ResolveSpikeRoot(); }
            catch (Exception ex) { return "<unresolved: " + ex.Message + ">"; }
        }
    }
}
