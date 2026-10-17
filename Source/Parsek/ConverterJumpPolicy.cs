using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Parsek
{
    /// <summary>
    /// The one converter policy for a Parsek time jump (todo
    /// TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND, TA-6): a jump is not production time
    /// (logistics design, "a Parsek jump is not production time"), whichever entry point made it
    /// (<see cref="TimeJumpManager.ExecuteForwardJump"/> or <see cref="TimeJumpManager.ExecuteJump"/>)
    /// and whether the vessel is loaded or not.
    ///
    /// <para>Stock <c>BaseConverter</c> (ISRU, drills, fuel cells, the science lab's
    /// <c>ModuleScienceConverter</c>) catches up <c>now - lastUpdateTime</c> in slices of
    /// <c>ResourceUtilities.GetMaxDeltaTime()</c> until the gap is closed (decompiled
    /// <c>BaseConverter.GetDeltaTime</c>), and an unloaded vessel keeps its timestamp in the
    /// module snapshot (<c>OnSave</c> writes <c>lastUpdateTime</c>), so it catches up everything
    /// the next time it loads. The policy shifts the timestamp forward by the jump delta: the
    /// jumped span is removed, and any catch-up the vessel already owed from before the jump
    /// (an unloaded miner left running) is kept. A zero timestamp is stock's "never ran"
    /// sentinel (stock resets it to now without catching up) and is left alone.</para>
    /// </summary>
    internal static class ConverterJumpPolicy
    {
        private const string Tag = "TimeJump";
        internal const string TimestampKey = "lastUpdateTime";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal enum ShiftOutcome
        {
            Shifted,
            NeverRan,
            NoJump,
            NoValue,
            Unparseable
        }

        /// <summary>
        /// Pure: the converter timestamp after a jump from <paramref name="preJumpUT"/> to
        /// <paramref name="postJumpUT"/>. Shifted by the delta and clamped to the post-jump UT
        /// (a timestamp already ahead of the clock never moves further ahead).
        /// </summary>
        internal static ShiftOutcome ComputeShiftedTimestamp(
            double oldTime, double preJumpUT, double postJumpUT, out double newTime)
        {
            newTime = oldTime;
            if (Math.Abs(oldTime) < 1e-9)
                return ShiftOutcome.NeverRan;

            double delta = postJumpUT - preJumpUT;
            if (!(delta > 0.0) || double.IsNaN(oldTime) || double.IsInfinity(oldTime))
                return ShiftOutcome.NoJump;

            newTime = Math.Min(oldTime + delta, postJumpUT);
            return ShiftOutcome.Shifted;
        }

        /// <summary>
        /// Pure over a ConfigNode: shifts the <c>lastUpdateTime</c> value of one unloaded
        /// converter's module snapshot (<c>ProtoPartModuleSnapshot.moduleValues</c>).
        /// </summary>
        internal static ShiftOutcome ShiftModuleSnapshot(
            ConfigNode moduleValues, double preJumpUT, double postJumpUT,
            out double oldTime, out double newTime)
        {
            oldTime = 0.0;
            newTime = 0.0;
            if (moduleValues == null || !moduleValues.HasValue(TimestampKey))
                return ShiftOutcome.NoValue;

            string raw = moduleValues.GetValue(TimestampKey);
            if (!double.TryParse(raw, NumberStyles.Float, ic, out oldTime))
                return ShiftOutcome.Unparseable;

            ShiftOutcome outcome = ComputeShiftedTimestamp(oldTime, preJumpUT, postJumpUT, out newTime);
            if (outcome == ShiftOutcome.Shifted)
                moduleValues.SetValue(TimestampKey, newTime.ToString("R", ic));
            return outcome;
        }

        /// <summary>Per-jump tallies, logged as one summary line.</summary>
        internal struct JumpTally
        {
            internal int LoadedShifted;
            internal int UnloadedShifted;
            internal int NeverRan;
            internal int Unreadable;
            internal int UnloadedVessels;

            internal void Count(ShiftOutcome outcome, bool loaded)
            {
                switch (outcome)
                {
                    case ShiftOutcome.Shifted:
                        if (loaded) LoadedShifted++; else UnloadedShifted++;
                        break;
                    case ShiftOutcome.NeverRan:
                        NeverRan++;
                        break;
                    case ShiftOutcome.NoValue:
                    case ShiftOutcome.Unparseable:
                        Unreadable++;
                        break;
                }
            }
        }

        /// <summary>
        /// Pure over the snapshots of one unloaded vessel: shifts every module the predicate
        /// classifies as a converter. Modules are given as (moduleName, moduleValues) pairs.
        /// </summary>
        internal static void ShiftUnloadedVesselModules(
            IList<KeyValuePair<string, ConfigNode>> modules,
            Func<string, bool> isConverterModule,
            double preJumpUT, double postJumpUT,
            ref JumpTally tally)
        {
            if (modules == null || isConverterModule == null)
                return;
            for (int i = 0; i < modules.Count; i++)
            {
                if (!isConverterModule(modules[i].Key))
                    continue;
                ShiftOutcome outcome = ShiftModuleSnapshot(
                    modules[i].Value, preJumpUT, postJumpUT, out _, out _);
                tally.Count(outcome, loaded: false);
            }
        }

        private static readonly FieldInfo lastUpdateTimeField =
            typeof(BaseConverter).GetField(TimestampKey,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly Dictionary<string, bool> converterModuleNameCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Runtime: is the named PartModule class a stock or modded BaseConverter subclass.</summary>
        internal static bool IsConverterModuleName(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName))
                return false;
            bool cached;
            if (converterModuleNameCache.TryGetValue(moduleName, out cached))
                return cached;
            Type type = AssemblyLoader.GetClassByName(typeof(PartModule), moduleName);
            bool isConverter = type != null && typeof(BaseConverter).IsAssignableFrom(type);
            converterModuleNameCache[moduleName] = isConverter;
            return isConverter;
        }

        /// <summary>
        /// Runtime: applies the policy to every non-ghost vessel in the scene, the loaded ones
        /// through the live module field and the unloaded ones through their proto snapshots
        /// (which is what the next load and every save read). Called by both jump entry points
        /// right after the clock moves.
        /// </summary>
        internal static void Apply(double preJumpUT, double postJumpUT, string jumpKind)
        {
            var tally = new JumpTally();
            List<Vessel> vessels = FlightGlobals.Vessels != null
                ? new List<Vessel>(FlightGlobals.Vessels)
                : new List<Vessel>();

            for (int vi = 0; vi < vessels.Count; vi++)
            {
                Vessel v = vessels[vi];
                if (v == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                if (v.loaded)
                    ApplyLoaded(v, preJumpUT, postJumpUT, ref tally);
                else
                    ApplyUnloaded(v, preJumpUT, postJumpUT, ref tally);
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Converter jump policy ({0}): pre={1:F1} post={2:F1} loadedShifted={3} " +
                    "unloadedShifted={4} unloadedVessels={5} neverRan={6} unreadable={7}",
                    jumpKind ?? "?", preJumpUT, postJumpUT, tally.LoadedShifted,
                    tally.UnloadedShifted, tally.UnloadedVessels, tally.NeverRan, tally.Unreadable));
        }

        private static void ApplyLoaded(Vessel v, double pre, double post, ref JumpTally tally)
        {
            if (v.parts == null) return;
            if (lastUpdateTimeField == null)
            {
                tally.Unreadable++;
                return;
            }
            for (int pi = 0; pi < v.parts.Count; pi++)
            {
                Part p = v.parts[pi];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var converter = p.Modules[m] as BaseConverter;
                    if (converter == null) continue;
                    double oldTime = (double)lastUpdateTimeField.GetValue(converter);
                    ShiftOutcome outcome = ComputeShiftedTimestamp(oldTime, pre, post, out double newTime);
                    if (outcome == ShiftOutcome.Shifted)
                    {
                        lastUpdateTimeField.SetValue(converter, newTime);
                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "Converter timestamp shifted (loaded): vessel={0} part={1} module={2} old={3:F1} new={4:F1}",
                                Recording.ResolveLocalizedName(v.vesselName), p.partInfo?.name ?? "?",
                                converter.GetType().Name, oldTime, newTime));
                    }
                    tally.Count(outcome, loaded: true);
                }
            }
        }

        private static void ApplyUnloaded(Vessel v, double pre, double post, ref JumpTally tally)
        {
            ProtoVessel pv = v.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return;
            int before = tally.UnloadedShifted;
            var modules = new List<KeyValuePair<string, ConfigNode>>();
            for (int pi = 0; pi < pv.protoPartSnapshots.Count; pi++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[pi];
                if (pps == null || pps.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null) continue;
                    modules.Add(new KeyValuePair<string, ConfigNode>(mod.moduleName, mod.moduleValues));
                }
            }
            ShiftUnloadedVesselModules(modules, IsConverterModuleName, pre, post, ref tally);
            int shifted = tally.UnloadedShifted - before;
            if (shifted > 0)
            {
                tally.UnloadedVessels++;
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Converter timestamps shifted (unloaded): vessel={0} pid={1} modules={2} delta={3:F1}",
                        Recording.ResolveLocalizedName(v.vesselName), v.persistentId, shifted, post - pre));
            }
        }
    }
}
