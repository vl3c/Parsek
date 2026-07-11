using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure formatter + emit for the <c>MissionMark</c> verb (P5.3). Emits a stable,
    /// grep-able <c>[Parsek][INFO][TestCommands] MISSIONMARK label=&lt;label&gt;
    /// ut=&lt;ut&gt;</c> log line that the external orchestrator correlates against its
    /// own timeline (an H3-style checkpoint marker). The message shape is a load-bearing
    /// contract: if it drifts, the orchestration correlation breaks, so the format is
    /// pinned by a log-assertion test. <c>ut</c> renders <c>none</c> when no game is
    /// loaded (MissionMark is valid in any scene, including the menus).
    /// </summary>
    internal static class TestCommandMissionMark
    {
        internal const string Tag = "TestCommands";

        /// <summary>The stable message body (without the <c>[Parsek][LEVEL][Sub]</c> prefix).</summary>
        internal static string FormatMarkMessage(string label, double? ut)
        {
            string utText = ut.HasValue ? ut.Value.ToString("R", CultureInfo.InvariantCulture) : "none";
            return "MISSIONMARK label=" + (label ?? string.Empty) + " ut=" + utText;
        }

        /// <summary>Emits the stable MISSIONMARK line at Info level.</summary>
        internal static void EmitMark(string label, double? ut)
            => ParsekLog.Info(Tag, FormatMarkMessage(label, ut));
    }
}
