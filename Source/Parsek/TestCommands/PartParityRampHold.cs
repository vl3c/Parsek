using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The start route's real side as a load leaves it (design-part-ghost-parity.md E.4, "Start-route
    /// reload"; todo PARTPARITY-START-ROUTE-RAMP-NOT-RELOADED). The start-route ghost is built from a
    /// snapshot of the burning part, and a snapshot of a ramping <c>ModuleColorChanger</c> says only
    /// which end the ramp runs to. Decompiled KSP 1.12.5:
    /// <list type="bullet">
    /// <item><c>animState</c> is the one persisted ramp field (<c>isPersistant</c>);
    /// <c>currentRateState</c> is protected, not a KSPField, and <c>Start()</c> sets it to
    /// <c>animState ? 1 : 0</c>. <c>FXModuleThrottleEffects.state</c>, the setter's level, is
    /// persisted, so a load changes nothing else the bell shows.</item>
    /// <item>What is shown each frame is written by the modules' own <c>Update</c>
    /// (<c>FXModuleThrottleEffects.Update</c> through <c>SetScalar</c>, and
    /// <c>ModuleColorChanger.Update</c>, which KSPCommunityFixes replaces by one that writes only
    /// while the ramp moves).</item>
    /// </list>
    /// So writing <c>currentRateState</c> to the loaded value, shown once, and letting the game run
    /// one frame shows what a load shows, under either <c>Update</c>. The ramp is put back where it would stand
    /// by then, one frame before anything else is captured. This file holds the pure rules and one
    /// ramp's hold; <c>PartParityFxDriver.StartReload.cs</c> the capture pass.
    /// </summary>
    internal sealed class PartParityRampHold
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary><c>ModuleColorChanger</c>'s protected ramp position (decompiled).</summary>
        internal const string FieldRamp = "currentRateState";

        /// <summary>The sidecar flag of a case whose start route was compared with the loaded real
        /// part: <c>start-route-reloaded:&lt;ramps moved&gt;</c>. Written exactly when the atlas
        /// carries the <c>real-start</c> row.</summary>
        internal const string FlagReloadedPrefix = "start-route-reloaded:";

        /// <summary>A ramp this close to the end a load puts it at stands there already.</summary>
        internal const float StandingEpsilon = 1e-3f;

        private readonly ModuleColorChanger module;
        private readonly FieldInfo field;
        private readonly float saved;
        private readonly double savedUT;

        private PartParityRampHold(ModuleColorChanger module, FieldInfo field, float saved, double ut)
        {
            this.module = module;
            this.field = field;
            this.saved = saved;
            savedUT = ut;
        }

        /// <summary>Where <c>ModuleColorChanger.Start()</c> puts a ramp.</summary>
        internal static float LoadedRamp(bool animState) => animState ? 1f : 0f;

        /// <summary>A load moves this ramp: it runs by rate and stands elsewhere than where a load
        /// puts it.</summary>
        internal static bool MovedByLoad(bool useRate, bool animState, float ramp)
            => useRate && !float.IsNaN(ramp) && Math.Abs(ramp - LoadedRamp(animState)) > StandingEpsilon;

        /// <summary>Where the ramp stands <paramref name="seconds"/> after <paramref name="ramp"/>:
        /// toward its end at <c>animRate</c> a second, stopping there (stock's own step).</summary>
        internal static float RampAfter(float ramp, bool animState, float animRate, double seconds)
        {
            double step = seconds > 0.0 && animRate > 0f ? animRate * seconds : 0.0;
            return (float)(animState ? Math.Min(1.0, ramp + step) : Math.Max(0.0, ramp - step));
        }

        internal static string Flag(int moved) => FlagReloadedPrefix + moved.ToString(Ic);

        /// <summary>The colour changers of a live part whose ramp a load would move, none written
        /// yet. <paramref name="unreadable"/>: the field is not there, so nothing can be said.</summary>
        internal static List<PartParityRampHold> Find(Part part, double ut, out bool unreadable)
        {
            unreadable = false;
            var list = new List<PartParityRampHold>();
            if (part == null || part.Modules == null) return list;
            FieldInfo f = RampField();
            if (f == null) { unreadable = true; return list; }
            foreach (PartModule pm in part.Modules)
            {
                if (!(pm is ModuleColorChanger cc)) continue;
                float ramp = (float)f.GetValue(cc);
                if (MovedByLoad(cc.useRate, cc.animState, ramp)) list.Add(new PartParityRampHold(cc, f, ramp, ut));
            }
            return list;
        }

        /// <summary>The ramp field of the game's own <c>ModuleColorChanger</c>, or null.</summary>
        internal static FieldInfo RampField()
        {
            FieldInfo f = typeof(ModuleColorChanger).GetField(FieldRamp,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null && f.FieldType == typeof(float) ? f : null;
        }

        /// <summary>Puts the ramp where a load puts it and shows it once, as KSPCommunityFixes' postfix
        /// of <c>Start()</c> does (its <c>Update</c> never writes a standing ramp, and a colour changer
        /// with no setter has no other writer); the game's next frame then writes what it writes.</summary>
        internal void Load()
        {
            float v = LoadedRamp(module.animState);
            field.SetValue(module, v);
            module.SetState(v);
        }

        /// <summary>Puts the ramp where it would stand at <paramref name="ut"/> had it run on.</summary>
        internal float Restore(double ut)
        {
            float v = RampAfter(saved, module.animState, module.animRate, ut - savedUT);
            field.SetValue(module, v);
            return v;
        }

        /// <summary>The module still exists (a lost vessel's does not).</summary>
        internal bool Alive => module != null;

        internal string Describe()
            => (module.moduleID ?? "-") + "." + (module.shaderProperty ?? "-") + "@" + module.animRate.ToString("0.000", Ic) +
               "/s " + saved.ToString("0.000", Ic) + "->" + LoadedRamp(module.animState).ToString("0", Ic);
    }
}
