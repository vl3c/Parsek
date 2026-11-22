using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>The host a sweep label was started against (<c>host.txt</c>): its
    /// persistent id, part count and situation at the label's first run step.</summary>
    internal sealed class PartParityHostBaseline
    {
        internal uint Pid;
        internal int PartCount;
        internal string Situation;

        /// <summary>The run kind the label was started as (<c>mode=</c>, design D.6): an air
        /// run's host sits at the pad, an orbit run's host orbits. Absent reads as orbit.</summary>
        internal bool Air;
    }

    /// <summary>One look at the host during a step: the vessel with the baseline's pid.</summary>
    internal sealed class PartParityHostObservation
    {
        /// <summary>A live vessel with the baseline's pid exists (not dead, root part present).</summary>
        internal bool Present;

        /// <summary>That vessel is the active vessel (the one the spawns are placed against).</summary>
        internal bool Active;

        internal int PartCount;
        internal bool Orbiting;
        internal string Situation;
    }

    /// <summary>A recorded host damage (<c>hostdamage.txt</c>): why the sweep stopped.</summary>
    internal sealed class PartParityHostDamage
    {
        /// <summary>One of <see cref="PartParityHostIntegrity.DamageReasons"/>.</summary>
        internal string Reason;
        internal string Detail;
    }

    /// <summary>
    /// The host-integrity guard of the part parity sweep (design-part-ghost-parity.md 4.1,
    /// 5.3): pure decisions and the two small files that carry it across steps. The host
    /// is the instrument's reference frame (every case spawns co-orbital with it and the
    /// time step needs it intact); once it lost parts, left its orbit or vanished, every
    /// later case would record a setup fault (or worse, a capture against a moving
    /// reference) that is the instrument's fault, not the part's. So the sweep stops at
    /// once, leaves the remaining cases unrun, and every later step of the label answers
    /// at once with the same damage.
    /// </summary>
    internal static class PartParityHostIntegrity
    {
        /// <summary>The label's host baseline, written by its first run step after a reset.</summary>
        internal const string BaselineFileName = "host.txt";

        /// <summary>The damage record; once present every later run step stops at once.</summary>
        internal const string DamageFileName = "hostdamage.txt";

        internal const string DamageNone = "none";

        /// <summary>No live vessel carries the baseline pid, or it is no longer active.</summary>
        internal const string DamageGone = "gone";

        /// <summary>The host has fewer parts than at the baseline.</summary>
        internal const string DamageParts = "parts";

        /// <summary>The host is no longer ORBITING (an orbit run), or no longer PRELAUNCH or
        /// LANDED (an air run).</summary>
        internal const string DamageSituation = "situation";

        /// <summary>The baseline's <c>mode=</c> values; an orbit baseline writes none, so its
        /// file stays what it was before air runs existed.</summary>
        internal const string ModeOrbit = "orbit";
        internal const string ModeAir = "air";

        /// <summary>The situations an air run's host keeps (design D.4).</summary>
        internal static readonly string[] AirHostSituations = { "PRELAUNCH", "LANDED" };

        /// <summary>The damage vocabulary, in decision order.</summary>
        internal static readonly string[] DamageReasons = { DamageGone, DamageParts, DamageSituation };

        /// <summary>The literal of the one Warn line written when the guard trips.</summary>
        internal const string DamageLineLiteral = "PartParity host damaged";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Null while the host is intact, else the damage reason: <c>gone</c> (no live
        /// vessel with the baseline pid, or it is not the active vessel), then <c>parts</c>
        /// (fewer parts than the baseline; a gain is not damage), then <c>situation</c> (no
        /// longer ORBITING; for an air run's baseline, no longer PRELAUNCH or LANDED). A
        /// missing baseline or observation decides nothing.
        /// </summary>
        internal static string Decide(PartParityHostBaseline baseline, PartParityHostObservation now)
        {
            if (baseline == null || now == null) return null;
            if (!now.Present || !now.Active) return DamageGone;
            if (now.PartCount < baseline.PartCount) return DamageParts;
            if (!SituationHolds(baseline.Air, now)) return DamageSituation;
            return null;
        }

        /// <summary>The host's situation is the one its run needs: ORBITING for an orbit run,
        /// PRELAUNCH or LANDED for an air run.</summary>
        internal static bool SituationHolds(bool airRun, PartParityHostObservation now)
        {
            if (now == null) return false;
            if (!airRun) return now.Orbiting;
            return now.Situation != null && Array.IndexOf(AirHostSituations, now.Situation) >= 0;
        }

        /// <summary>The damage detail: what changed against the baseline, one line.</summary>
        internal static string Describe(string reason, PartParityHostBaseline baseline, PartParityHostObservation now, string where)
        {
            var sb = new StringBuilder();
            sb.Append("pid=").Append(baseline != null ? baseline.Pid.ToString(Ic) : "<none>");
            switch (reason)
            {
                case DamageGone:
                    sb.Append(now != null && now.Present ? " no longer the active vessel" : " no live vessel");
                    break;
                case DamageParts:
                    sb.Append(" parts ").Append(baseline != null ? baseline.PartCount.ToString(Ic) : "?")
                        .Append("->").Append(now != null ? now.PartCount.ToString(Ic) : "?");
                    break;
                case DamageSituation:
                    sb.Append(" situation ").Append(baseline?.Situation ?? "?")
                        .Append("->").Append(now?.Situation ?? "?");
                    break;
            }
            if (!string.IsNullOrEmpty(where)) sb.Append(" at ").Append(where);
            return OneLine(sb.ToString());
        }

        /// <summary>The one Warn line: <c>PartParity host damaged label= reason= detail=</c>
        /// and what the sweep does about it.</summary>
        internal static string DamageLine(string label, PartParityHostDamage damage, int casesLeft)
            => DamageLineLiteral + " label=" + (label ?? string.Empty)
               + " reason=" + (damage?.Reason ?? DamageNone)
               + " detail=" + (damage?.Detail ?? "<none>")
               + ": the sweep stops; " + casesLeft.ToString(Ic)
               + " case(s) left unrun and every later step of this label stops at once";

        /// <summary>The payload's <c>hostDamage=</c> token: the reason, or <c>none</c>.</summary>
        internal static string PayloadToken(PartParityHostDamage damage)
            => damage != null && !string.IsNullOrEmpty(damage.Reason) ? damage.Reason : DamageNone;

        // ----- files -----

        internal static string FormatBaseline(PartParityHostBaseline b)
        {
            if (b == null) return string.Empty;
            return "pid=" + b.Pid.ToString(Ic) + "\n"
                   + "parts=" + b.PartCount.ToString(Ic) + "\n"
                   + "situation=" + OneLine(b.Situation ?? string.Empty) + "\n"
                   + (b.Air ? "mode=" + ModeAir + "\n" : string.Empty);
        }

        /// <summary>Null when the text holds no valid <c>pid=</c> and <c>parts=</c>.</summary>
        internal static PartParityHostBaseline ParseBaseline(string text)
        {
            Dictionary<string, string> kv = ParseKeyValues(text);
            if (!kv.TryGetValue("pid", out string pidText)
                || !uint.TryParse(pidText, NumberStyles.None, Ic, out uint pid)
                || !kv.TryGetValue("parts", out string partsText)
                || !int.TryParse(partsText, NumberStyles.None, Ic, out int parts))
                return null;
            kv.TryGetValue("situation", out string situation);
            kv.TryGetValue("mode", out string mode);
            return new PartParityHostBaseline
            {
                Pid = pid, PartCount = parts, Situation = situation,
                Air = string.Equals(mode, ModeAir, StringComparison.Ordinal),
            };
        }

        internal static string FormatDamage(PartParityHostDamage d)
        {
            if (d == null) return string.Empty;
            return "reason=" + OneLine(d.Reason ?? string.Empty) + "\n"
                   + "detail=" + OneLine(d.Detail ?? string.Empty) + "\n";
        }

        /// <summary>Null when the text names no known reason. A record whose reason is not
        /// in the vocabulary (a torn or foreign file) still stops the sweep: it reads as
        /// <c>gone</c> with the raw text as its detail, so a damaged label never resumes.</summary>
        internal static PartParityHostDamage ParseDamage(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return null;
            Dictionary<string, string> kv = ParseKeyValues(text);
            kv.TryGetValue("reason", out string reason);
            kv.TryGetValue("detail", out string detail);
            if (reason == null || Array.IndexOf(DamageReasons, reason) < 0)
                return new PartParityHostDamage { Reason = DamageGone, Detail = "unreadable hostdamage.txt: " + OneLine(text.Trim()) };
            return new PartParityHostDamage { Reason = reason, Detail = detail };
        }

        // ----- the case vessel -----

        /// <summary>True when a settled case's real vessel is no longer there to capture: the
        /// vessel or its root part is gone (a destroyed Unity object reads null), or it is
        /// DEAD. A case lost this way is <c>died-settling</c>, never a prepare crash.</summary>
        internal static bool IsCaseVesselLost(bool vesselPresent, bool rootPartPresent, bool vesselDead)
            => !vesselPresent || !rootPartPresent || vesselDead;

        private static Dictionary<string, string> ParseKeyValues(string text)
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return kv;
            foreach (string raw in text.Split(new[] { '\r', '\n' }))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim();
                if (!kv.ContainsKey(k)) kv[k] = raw.Substring(eq + 1).Trim();
            }
            return kv;
        }

        private static string OneLine(string s)
            => (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    }
}
