using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Captures every celestial body's STOCK <c>timeWarpAltitudeLimits</c> array once,
    /// before any mod overrides it, and serves the stock value back to Parsek.
    ///
    /// Why: <see cref="FlightRecorder.ComputeApproachAltitude(CelestialBody)"/> uses
    /// <c>timeWarpAltitudeLimits[4]</c> (KSP's 100x-warp altitude limit) as the airless-body
    /// recording-split approach threshold — it is KSP's own definition of "close enough that
    /// fast warp is dangerous" and adapts to modded planets automatically. BetterTimeWarp
    /// (and other warp mods) overwrite that array at <c>MainMenu</c> startup — BTW writes
    /// <c>{0,0,0,0,0,0,100000,2000000}</c> for every body, zeroing index [4]. The
    /// <see cref="CelestialBody"/> objects persist for the whole KSP session, so by the time
    /// Parsek reads the array in flight it is already zeroed and the split threshold silently
    /// degrades to a radius fallback.
    ///
    /// Fix: snapshot the stock array on <c>PSystemManager.Instance.OnPSystemReady</c>, which
    /// fires during the <c>PSystemSpawn</c> startup phase — BEFORE the <c>MainMenu</c> addons
    /// that override the limits run. (Same proven hook ContractConfigurator uses.) If the
    /// capture never runs the cache stays empty and callers fall back to exactly the prior
    /// behavior, so this is fail-safe.
    /// </summary>
    internal static class StockWarpAltitudeLimits
    {
        internal const string Tag = "StockWarpLimits";

        private static readonly Dictionary<string, float[]> stockLimits =
            new Dictionary<string, float[]>(StringComparer.Ordinal);

        private static bool captured;

        /// <summary>True once a non-empty capture has run.</summary>
        internal static bool HasCaptured => captured;

        /// <summary>
        /// KSP-bound capture: snapshot the live <c>timeWarpAltitudeLimits</c> of every loaded
        /// body and store it. Must be called before any mod overrides the arrays (i.e. on
        /// <c>OnPSystemReady</c>) for the stored values to be the genuine stock limits.
        /// </summary>
        internal static void CaptureFromBodies(string reason)
        {
            var pairs = new List<KeyValuePair<string, float[]>>();
            var bodies = FlightGlobals.Bodies;
            if (bodies != null)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    if (body == null)
                        continue;
                    pairs.Add(new KeyValuePair<string, float[]>(body.name, body.timeWarpAltitudeLimits));
                }
            }
            Capture(pairs, reason);
        }

        /// <summary>
        /// Pure capture core: fill the cache from (body name, limits) pairs. Clones each array
        /// so later mod overrides of the live array cannot mutate the snapshot. Logs a single
        /// summary line (the in-game proof that the capture actually ran). Returns the count.
        /// </summary>
        internal static int Capture(IEnumerable<KeyValuePair<string, float[]>> bodyLimits, string reason)
        {
            int count = 0;
            if (bodyLimits != null)
            {
                foreach (var kv in bodyLimits)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null)
                        continue;
                    stockLimits[kv.Key] = (float[])kv.Value.Clone();
                    count++;
                }
            }

            if (count > 0)
                captured = true;

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "captured stock timeWarpAltitudeLimits for {0} bodies (reason={1})", count, reason));
            return count;
        }

        /// <summary>
        /// Look up the captured stock limit at <paramref name="index"/> for a body. Returns
        /// false (and value 0) when no snapshot exists, the array is absent, or the index is
        /// out of range — callers then fall back to the live array / radius.
        /// </summary>
        internal static bool TryGetStockLimit(string bodyName, int index, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(bodyName))
                return false;
            if (!stockLimits.TryGetValue(bodyName, out var arr))
                return false;
            if (arr == null || index < 0 || index >= arr.Length)
                return false;
            value = arr[index];
            return true;
        }

        /// <summary>Test seam: clear the cache between tests.</summary>
        internal static void ResetForTesting()
        {
            stockLimits.Clear();
            captured = false;
        }

        /// <summary>Test seam: inject a stock snapshot without a live KSP body.</summary>
        internal static void SeedForTesting(string bodyName, float[] limits)
        {
            if (string.IsNullOrEmpty(bodyName))
                return;
            stockLimits[bodyName] = limits == null ? null : (float[])limits.Clone();
            captured = true;
        }
    }

    /// <summary>
    /// Registers the stock-warp-limit snapshot on <c>OnPSystemReady</c>, which fires during the
    /// <c>PSystemSpawn</c> startup phase — before the <c>MainMenu</c> addons (e.g. BetterTimeWarp)
    /// that overwrite <c>timeWarpAltitudeLimits</c>. Mirrors ContractConfigurator's pattern.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.PSystemSpawn, true)]
    internal sealed class StockWarpAltitudeLimitsCapture : MonoBehaviour
    {
        private void Start()
        {
            try
            {
                var psm = PSystemManager.Instance;
                if (psm != null && psm.OnPSystemReady != null)
                {
                    psm.OnPSystemReady.Add(OnPSystemReady);
                    ParsekLog.Verbose(StockWarpAltitudeLimits.Tag,
                        "registered OnPSystemReady stock-warp-limit capture");
                }
                else
                {
                    // No PSystem event available — capture whatever bodies already exist.
                    StockWarpAltitudeLimits.CaptureFromBodies("psystemspawn-fallback");
                }
            }
            catch (Exception e)
            {
                ParsekLog.Warn(StockWarpAltitudeLimits.Tag, "Start failed: " + e.Message);
            }
        }

        private void OnPSystemReady()
        {
            StockWarpAltitudeLimits.CaptureFromBodies("OnPSystemReady");
        }
    }
}
