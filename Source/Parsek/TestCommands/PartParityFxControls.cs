using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// How an FX control (design-part-ghost-parity.md E.5) keeps its evidence across steps. The
    /// control's own sidecar is the only state a later step reads back, and its key tables are
    /// mirrored, so the evidence its judge needs (<see cref="PartParityFxControlEvidence"/>) is
    /// written as one flag, <c>fx-evidence:&lt;key&gt;=&lt;value&gt;;...</c>, and read back from it:
    /// the same judge runs in the step that flew the control and in every later one. Pure.
    /// </summary>
    internal static class PartParityFxControls
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal const string EvidenceFlagPrefix = "fx-evidence:";

        /// <summary>An optional control with no subject on this install: its judge answers
        /// <c>n/a</c>, never <c>bad</c> (design E.5).</summary>
        internal const string NotApplicableFlag = "fx-control-not-applicable";

        /// <summary>The evidence flag of one control case.</summary>
        internal static string FormatEvidence(PartParityFxControlEvidence e)
        {
            if (e == null) return null;
            var failing = new List<string>();
            foreach (string m in PartParityFxRules.Measures) if (e.FailingMeasures.Contains(m)) failing.Add(m);
            var sb = new StringBuilder(EvidenceFlagPrefix);
            sb.Append("core=").Append(Bit(e.RealCorePresent))
                .Append(";events=").Append(e.EventsRecorded.ToString(Ic))
                .Append(";allZero=").Append(Bit(e.AllCoverageZero))
                .Append(";ghostZero=").Append(Bit(e.GhostCoverageZero))
                .Append(";realNonZero=").Append(Bit(e.RealCoverageNonZero))
                .Append(";plateLit=").Append(Bit(e.RealPlateLit))
                .Append(";plateFail=").Append(Bit(e.PlateViewsFail))
                .Append(";failing=").Append(string.Join(",", failing.ToArray()));
            return sb.ToString();
        }

        /// <summary>The evidence a sidecar's flags carry (its setup status stamped in), or null
        /// when it carries none.</summary>
        internal static PartParityFxControlEvidence ParseEvidence(IList<string> flags, string setupStatus)
        {
            if (flags == null) return null;
            foreach (string f in flags)
            {
                if (f == null || !f.StartsWith(EvidenceFlagPrefix, StringComparison.Ordinal)) continue;
                var e = new PartParityFxControlEvidence { SetupStatus = setupStatus ?? TestCommandPartParitySweep.SetupOk };
                foreach (string pair in f.Substring(EvidenceFlagPrefix.Length).Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq), value = pair.Substring(eq + 1);
                    switch (key)
                    {
                        case "core": e.RealCorePresent = value == "1"; break;
                        case "events":
                            int.TryParse(value, NumberStyles.Integer, Ic, out int events);
                            e.EventsRecorded = events;
                            break;
                        case "allZero": e.AllCoverageZero = value == "1"; break;
                        case "ghostZero": e.GhostCoverageZero = value == "1"; break;
                        case "realNonZero": e.RealCoverageNonZero = value == "1"; break;
                        case "plateLit": e.RealPlateLit = value == "1"; break;
                        case "plateFail": e.PlateViewsFail = value == "1"; break;
                        case "failing":
                            foreach (string m in value.Split(','))
                                if (m.Length > 0) e.FailingMeasures.Add(m);
                            break;
                    }
                }
                return e;
            }
            return null;
        }

        /// <summary>One FX control's outcome from its sidecar: <c>n/a</c> for an optional control
        /// with no subject, the heat control on its P6 views, every other on its evidence.</summary>
        internal static string Judge(string id, PartParityCaseSidecar s, out string detail)
        {
            detail = null;
            if (s == null) { detail = "no sidecar"; return TestCommandPartParitySweep.ControlsBad; }
            if (s.Flags != null && s.Flags.Contains(NotApplicableFlag))
            {
                detail = "no eligible subject on this install";
                return TestCommandPartParitySweep.ControlsNotApplicable;
            }
            if (PartParityBurstPlan.IsBuiltControl(id))
                return PartParityBurstPlan.JudgeControl(id, s.Flags, s.Setup?.Status, out detail);
            if (id == TestCommandPartParitySweep.ControlHeatEventsDropped)
                return PartParityFxRules.JudgeHeatEventsDroppedControl(s.StateProof?.Reached == true,
                    s.StateProof?.Events?.Count ?? 0, s.Views, s.Setup?.Status, out detail);
            PartParityFxControlEvidence e = ParseEvidence(s.Flags, s.Setup?.Status);
            if (e == null && TestCommandPartParitySweep.IsSetupFault(s.Setup?.Status))
                e = new PartParityFxControlEvidence { SetupStatus = s.Setup.Status };
            return PartParityFxRules.JudgeFxControl(id, e, out detail);
        }

        private static string Bit(bool b) => b ? "1" : "0";
    }
}
