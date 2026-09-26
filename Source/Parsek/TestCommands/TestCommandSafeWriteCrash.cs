using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>SafeWriteCrash</c> verb (D16 <c>safe-write</c>):
    /// a crash after <c>FileIOUtils</c> wrote <c>&lt;path&gt;.tmp</c> but before the swap,
    /// injected inside ONE boot. Three phases, driven in order by a lane:
    /// <list type="bullet">
    /// <item><c>phase=arm recording=&lt;id&gt;</c> records the committed recording's <c>.prec</c>
    /// digest and point count, arms <c>FileIOUtils.ArmCrashAfterTemp</c> on
    /// <c>&lt;id&gt;.prec</c> and marks the recording dirty so the next save rewrites it.</item>
    /// <item><c>phase=coldreload</c> runs the product's own isolated-restore prep, so the NEXT
    /// <c>LoadGame</c> takes the cold path a process restart would (sidecars re-read from
    /// disk, orphan sweep) instead of keeping the in-memory store.</item>
    /// <item><c>phase=probe</c> reports read-only: fired count, whether the <c>.prec</c> still
    /// equals the armed digest, the transient residue left next to it, and the loaded
    /// recording's point count against the armed one.</item>
    /// </list>
    /// </summary>
    internal static class TestCommandSafeWriteCrash
    {
        internal const string Verb = "SafeWriteCrash";
        internal const string PhaseKey = "phase";
        internal const string RecordingKey = "recording";

        internal const string PhaseArm = "arm";
        internal const string PhaseProbe = "probe";
        internal const string PhaseColdReload = "coldreload";

        internal static readonly string[] Phases = { PhaseArm, PhaseProbe, PhaseColdReload };

        internal static readonly string[] Reasons =
        {
            "safewritecrash-phase-arg-missing",
            "safewritecrash-phase-arg-invalid",
            "safewritecrash-recording-arg-missing",
            "safewritecrash-recording-not-found",
            "safewritecrash-no-sidecar",
            "safewritecrash-already-armed",
            "safewritecrash-arm-refused",
            "safewritecrash-no-baseline",
        };

        /// <summary>Validates <c>phase=</c>; returns null on success or the refusal reason.</summary>
        internal static string ValidatePhase(string phase)
        {
            if (string.IsNullOrEmpty(phase))
                return "safewritecrash-phase-arg-missing";
            return Array.IndexOf(Phases, phase) >= 0 ? null : "safewritecrash-phase-arg-invalid";
        }

        /// <summary>The path substring the hook is armed on: the recording's trajectory
        /// sidecar, which also matches its staged write (<c>&lt;id&gt;.prec.stage.&lt;guid&gt;</c>),
        /// the first file the sidecar commit batch writes.</summary>
        internal static string BuildPattern(string recordingId) => recordingId + ".prec";

        /// <summary>Counts the transient artifacts (<c>.tmp</c> / <c>.stage.</c> /
        /// <c>.bak.</c>) of <paramref name="recordingId"/>'s trajectory sidecar among
        /// <paramref name="fileNames"/>, by the orphan sweep's own classifier.</summary>
        internal static int CountResidue(IEnumerable<string> fileNames, string recordingId)
        {
            if (fileNames == null || string.IsNullOrEmpty(recordingId))
                return 0;
            string prefix = BuildPattern(recordingId);
            int n = 0;
            foreach (string name in fileNames)
            {
                if (name != null
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && RecordingStore.IsTransientSidecarArtifactFile(name))
                    n++;
            }
            return n;
        }

        /// <summary>First 16 hex chars of a digest; <c>none</c> for a missing file.</summary>
        internal static string ShortHex(byte[] digest)
        {
            if (digest == null)
                return "none";
            var sb = new StringBuilder(16);
            for (int i = 0; i < digest.Length && i < 8; i++)
                sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        internal static string FormatArmLine(string recordingId, string pattern, string digest, int points)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "safewritecrash armed recording={0} pattern={1} baselineDigest={2} baselinePoints={3}",
                recordingId, pattern, digest, points);
        }

        internal static string FormatProbeLine(
            string recordingId, int fired, bool armed, string digest, bool unchanged,
            int residue, int points, int baselinePoints, bool loaded, bool loadFailed)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "safewritecrash probe recording={0} fired={1} armed={2} digest={3} unchanged={4} " +
                "residue={5} loaded={6} loadFailed={7} points={8} baselinePoints={9}",
                recordingId, fired, armed ? "true" : "false", digest, unchanged ? "true" : "false",
                residue, loaded ? "true" : "false", loadFailed ? "true" : "false", points, baselinePoints);
        }
    }
}
