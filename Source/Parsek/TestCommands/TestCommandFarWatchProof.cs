using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>The sub-operations of the automation-only <c>FarWatchProof</c> verb.</summary>
    internal enum FarWatchProofOp
    {
        Arm,
        Build,
        Switch,
        Follow,
        MoveTo,
        Advance,
        Solver,
        Park,
        Destroy,
        Save,
        Autosave,
        Lock,
        Fence,
        Control,
        Unlist,
        Kill,
        Recalc,
        Report,
    }

    /// <summary>
    /// Pure half of the THROWAWAY far-watch step 1 proof verb (docs/dev/design-far-watch.md
    /// section 18.1). Never merged. Automation only: the seam addon exists only under
    /// <c>PARSEK_TEST_COMMANDS=1</c>, and nothing in the proof is patched or created until the
    /// first <c>FarWatchProof</c> step arrives, so a normal game is unaffected.
    /// <para>Grammar: <c>cmd=FarWatchProof op=&lt;op&gt; [args]</c>. Every op answers OK with a
    /// <c>result=</c> payload (ok, or the reason it did nothing); the observations are the
    /// <c>[FarWatchProof] obs=&lt;name&gt; key=value ...</c> log lines, not the verdict, so one
    /// failed precondition never stops the rest of the run from being measured.</para>
    /// </summary>
    internal static class TestCommandFarWatchProof
    {
        internal const string Verb = "FarWatchProof";
        internal const string Tag = "FarWatchProof";

        /// <summary>The verb's deferral budget: the harness deferred-step cap (540 s).
        /// An op that waits (advance, soak windows) time-boxes itself under it.</summary>
        internal const double BudgetSeconds = 540.0;

        /// <summary>The longest real time one waiting op may take.</summary>
        internal const double MaxOpSeconds = 480.0;

        internal const string OpKey = "op";

        internal static readonly string[] OpTokens =
        {
            "arm", "build", "switch", "follow", "moveto", "advance", "solver", "park",
            "destroy", "save", "autosave", "lock", "fence", "control", "unlist", "kill",
            "recalc", "report",
        };

        internal static readonly string[] ArgKeys =
        {
            "op", "variant", "target", "tree", "index", "watch", "untilUT", "maxRate", "pauses",
            "seconds", "name", "strip", "state", "alt", "body", "mode", "label", "interval",
        };

        internal const string ArgReasonUnknownOp = "farwatchproof-op-invalid";
        internal const string ArgReasonUnknownKey = "farwatchproof-arg-unknown";
        internal const string ArgReasonBadValue = "farwatchproof-arg-invalid";

        /// <summary>Parsed arguments (absent numeric args are NaN, absent strings null).</summary>
        internal struct Args
        {
            public FarWatchProofOp Op;
            public string Variant;
            public string Target;
            public string Tree;
            public int Index;
            public bool Watch;
            public double UntilUT;
            public double MaxRate;
            public int Pauses;
            public double Seconds;
            public string Name;
            public bool Strip;
            public string State;
            public double Alt;
            public string Body;
            public string Mode;
            public string Label;
            public double Interval;
        }

        internal static bool TryParseOp(string raw, out FarWatchProofOp op)
        {
            op = FarWatchProofOp.Report;
            if (string.IsNullOrEmpty(raw)) return false;
            for (int i = 0; i < OpTokens.Length; i++)
            {
                if (string.Equals(OpTokens[i], raw, StringComparison.Ordinal))
                {
                    op = (FarWatchProofOp)i;
                    return true;
                }
            }
            return false;
        }

        internal static string OpToken(FarWatchProofOp op)
        {
            int i = (int)op;
            return i >= 0 && i < OpTokens.Length ? OpTokens[i] : "?";
        }

        /// <summary>Parses the args; returns null on success or the refusal reason (with the
        /// offending key appended after a colon for the log).</summary>
        internal static string TryParseArgs(IDictionary<string, string> raw, out Args a)
        {
            a = new Args
            {
                Index = -1,
                UntilUT = double.NaN,
                MaxRate = double.NaN,
                Seconds = double.NaN,
                Alt = double.NaN,
                Interval = double.NaN,
                Strip = true,
            };
            string opRaw = Get(raw, OpKey);
            if (!TryParseOp(opRaw, out a.Op))
                return ArgReasonUnknownOp;
            if (raw != null)
            {
                foreach (string key in raw.Keys)
                {
                    if (Array.IndexOf(ArgKeys, key) < 0)
                        return ArgReasonUnknownKey + ":" + key;
                }
            }
            a.Variant = Get(raw, "variant");
            a.Target = Get(raw, "target");
            a.Tree = Get(raw, "tree");
            a.Name = Get(raw, "name");
            a.State = Get(raw, "state");
            a.Body = Get(raw, "body");
            a.Mode = Get(raw, "mode");
            a.Label = Get(raw, "label");
            string bad = null;
            if (!TryInt(raw, "index", ref a.Index)) bad = "index";
            if (!TryInt(raw, "pauses", ref a.Pauses)) bad = "pauses";
            if (!TryDouble(raw, "untilUT", ref a.UntilUT)) bad = "untilUT";
            if (!TryDouble(raw, "maxRate", ref a.MaxRate)) bad = "maxRate";
            if (!TryDouble(raw, "seconds", ref a.Seconds)) bad = "seconds";
            if (!TryDouble(raw, "alt", ref a.Alt)) bad = "alt";
            if (!TryDouble(raw, "interval", ref a.Interval)) bad = "interval";
            if (!TryBool(raw, "watch", ref a.Watch)) bad = "watch";
            if (!TryBool(raw, "strip", ref a.Strip)) bad = "strip";
            if (bad != null)
                return ArgReasonBadValue + ":" + bad;
            if (!double.IsNaN(a.Seconds) && (a.Seconds <= 0 || a.Seconds > MaxOpSeconds))
                return ArgReasonBadValue + ":seconds";
            if (a.Op == FarWatchProofOp.Switch && a.Target != "drone" && a.Target != "ship")
                return ArgReasonBadValue + ":target";
            if (a.Op == FarWatchProofOp.Advance && double.IsNaN(a.UntilUT))
                return ArgReasonBadValue + ":untilUT";
            return null;
        }

        // ----- The 6.3 terrain floor (pure) -----

        /// <summary>The design's terrain floor above the ground under the target:
        /// <c>base + max(0, -vUp * dt) + 0.5 * g * dt^2</c>, where dt is the GAME time until the
        /// next write (warp rate times the fixed step). Non-finite inputs fall back to base.</summary>
        internal static double TerrainFloorMeters(
            double verticalSpeed, double gameDtSeconds, double gravity, double baseMeters)
        {
            double floor = baseMeters;
            if (double.IsNaN(verticalSpeed) || double.IsInfinity(verticalSpeed)) verticalSpeed = 0;
            if (double.IsNaN(gameDtSeconds) || gameDtSeconds < 0 || double.IsInfinity(gameDtSeconds)) gameDtSeconds = 0;
            if (double.IsNaN(gravity) || gravity < 0 || double.IsInfinity(gravity)) gravity = 0;
            floor += Math.Max(0.0, -verticalSpeed * gameDtSeconds);
            floor += 0.5 * gravity * gameDtSeconds * gameDtSeconds;
            return floor;
        }

        /// <summary>The altitude the drone is written at: the target altitude, raised to the
        /// floor above the ground (over an ocean the ground is sea level).</summary>
        internal static double ClampAltitudeToFloor(
            double targetAltitude, double terrainAltitude, bool overOcean, double floorMeters,
            out bool clamped)
        {
            double ground = overOcean ? Math.Max(0.0, terrainAltitude) : terrainAltitude;
            if (double.IsNaN(ground) || double.IsInfinity(ground)) ground = 0;
            double min = ground + floorMeters;
            clamped = targetAltitude < min;
            return clamped ? min : targetAltitude;
        }

        /// <summary>The float grid step (metres) of a single-precision world coordinate at
        /// <paramref name="magnitude"/> metres from the floating origin: |p| * 2^-23.</summary>
        internal static double FloatGridStepMeters(double magnitude)
            => Math.Abs(magnitude) * (1.0 / 8388608.0);

        /// <summary>The highest warp index whose rate keeps at least <paramref name="realLeadSeconds"/>
        /// of real time before <paramref name="untilUT"/> and does not exceed
        /// <paramref name="maxRate"/>; 0 when none qualifies (the design's 8.4 ceiling shape).</summary>
        internal static int HighestWarpIndexBefore(
            IList<float> rates, double nowUT, double untilUT, double maxRate, double realLeadSeconds)
        {
            if (rates == null || rates.Count == 0) return 0;
            double left = untilUT - nowUT;
            int best = 0;
            for (int i = 0; i < rates.Count; i++)
            {
                double r = rates[i];
                if (!double.IsNaN(maxRate) && r > maxRate) break;
                if (r * realLeadSeconds <= left) best = i;
            }
            return best;
        }

        /// <summary>Whether a wait that started at <paramref name="startReal"/> has run out of
        /// its own time box (seconds arg, default <see cref="MaxOpSeconds"/>).</summary>
        internal static bool OpTimedOut(double startReal, double nowReal, double seconds)
        {
            double box = double.IsNaN(seconds) ? MaxOpSeconds : Math.Min(seconds, MaxOpSeconds);
            return nowReal - startReal >= box;
        }

        // ----- Formatting -----

        internal static string F(double v, string fmt = "F2")
        {
            if (double.IsNaN(v)) return "nan";
            if (double.IsPositiveInfinity(v)) return "inf";
            if (double.IsNegativeInfinity(v)) return "-inf";
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        internal static string B(bool b) => b ? "true" : "false";

        /// <summary>One grep-stable observation line body: <c>obs=&lt;name&gt; k=v k=v</c>.
        /// Values are sanitised (spaces become underscores) so every pair stays one token.</summary>
        internal static string FormatObs(string name, IList<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            sb.Append("obs=").Append(Sanitize(name));
            if (pairs != null)
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    sb.Append(' ').Append(pairs[i].Key).Append('=').Append(Sanitize(pairs[i].Value));
                }
            }
            return sb.ToString();
        }

        internal static string Sanitize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "-";
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                sb.Append(c == ' ' || c == '\t' || c == '\n' || c == '\r' ? '_' : c);
            }
            return sb.ToString();
        }

        private static string Get(IDictionary<string, string> raw, string key)
            => raw != null && raw.TryGetValue(key, out string v) ? v : null;

        private static bool TryDouble(IDictionary<string, string> raw, string key, ref double value)
        {
            string s = Get(raw, key);
            if (s == null) return true;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                || double.IsNaN(d) || double.IsInfinity(d))
                return false;
            value = d;
            return true;
        }

        private static bool TryInt(IDictionary<string, string> raw, string key, ref int value)
        {
            string s = Get(raw, key);
            if (s == null) return true;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
                return false;
            value = n;
            return true;
        }

        private static bool TryBool(IDictionary<string, string> raw, string key, ref bool value)
        {
            string s = Get(raw, key);
            if (s == null) return true;
            if (s == "true") { value = true; return true; }
            if (s == "false") { value = false; return true; }
            return false;
        }
    }
}
