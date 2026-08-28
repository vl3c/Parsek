using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM producer fix.
    ///
    /// <para>The fix lives at ONE line inside <c>FlightRecorder.OnVesselSOIChanged</c>, a
    /// handler that cannot be reached headlessly (it needs a live <c>Vessel</c> +
    /// <c>FlightGlobals</c>). Every behavioural cell in
    /// <see cref="SoiSeamDoubleEmitTests"/> drives the extracted helper directly, so
    /// reverting the handler back to <c>CloseCurrentTrackSection(soiUT)</c> +
    /// <c>StartNewTrackSection(currentEnv, ReferenceFrame.Absolute, soiUT)</c> would leave
    /// the whole xUnit suite green while restoring the defect on every on-rails SOI
    /// crossing. This gate is what closes that silent-revert hole: it pins the PRODUCTION
    /// WIRING (the handler routes through the helper, and the helper routes through the
    /// pure resolver) rather than any behaviour the other file already covers.</para>
    ///
    /// <para>Mirrors <see cref="PolylineDriverWalkDeleteGateTests"/> /
    /// <c>GhostOrbitLineCascadeDeleteGateTests</c> in mechanism (read the source, strip
    /// comments, assert over one brace-matched method body).</para>
    /// </summary>
    public class SoiSeamProducerWiringGateTests
    {
        private const string RecorderPath = "FlightRecorder.cs";

        [Fact]
        public void OnVesselSOIChanged_RoutesTheSeamThroughTheTransitionHelper()
        {
            string body = MethodBody("public void OnVesselSOIChanged(");

            Assert.Contains("TransitionTrackSectionAtSoiBoundary(", body);
            Assert.Contains("isOnRails", body);
        }

        [Fact]
        public void OnVesselSOIChanged_DoesNotOpenARawSectionItself()
        {
            // The defect WAS this handler opening its own section. Any direct
            // StartNewTrackSection here re-opens the hole regardless of the helper's
            // existence, and a literal ReferenceFrame.Absolute is the exact pre-fix line.
            string body = MethodBody("public void OnVesselSOIChanged(");

            Assert.False(body.Contains("StartNewTrackSection("),
                "SOI-seam gate: OnVesselSOIChanged must not open a TrackSection directly - "
                + "route through TransitionTrackSectionAtSoiBoundary so the seam's reference "
                + "frame follows the rails state (RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM).");
            Assert.False(body.Contains("ReferenceFrame.Absolute"),
                "SOI-seam gate: OnVesselSOIChanged must not name ReferenceFrame.Absolute - "
                + "the handler only runs while isOnRails is true, and an Absolute section "
                + "opened there can never receive a frame (OnPhysicsFrame early-returns on "
                + "isOnRails), which is what produced the INV2 double cover.");
        }

        [Fact]
        public void TransitionHelper_ResolvesTheFrameInsteadOfHardcodingOne()
        {
            // Second half of the wiring: the helper must ask the pure resolver. Hardcoding
            // a frame inside the helper would pass every behavioural cell that drives the
            // resolver directly while still shipping the wrong section at the seam.
            string body = MethodBody("internal void TransitionTrackSectionAtSoiBoundary(");

            Assert.Contains("ResolveSoiBoundarySectionFrame(", body);
            Assert.False(body.Contains("ReferenceFrame.Absolute"),
                "SOI-seam gate: TransitionTrackSectionAtSoiBoundary must take its frame from "
                + "ResolveSoiBoundarySectionFrame, never a literal.");
            Assert.False(body.Contains("ReferenceFrame.OrbitalCheckpoint"),
                "SOI-seam gate: TransitionTrackSectionAtSoiBoundary must take its frame from "
                + "ResolveSoiBoundarySectionFrame, never a literal.");
        }

        [Fact]
        public void TransitionHelper_LogsTheSeamTransition()
        {
            // CLAUDE.md logging requirement: the seam is one of four reference-frame
            // transitions and must be grep-sliceable like the other three.
            string body = MethodBody("internal void TransitionTrackSectionAtSoiBoundary(");

            Assert.Contains("Reference frame transition:", body);
            Assert.Contains("ParsekLog.Info(\"Recorder\"", body);
        }

        // ---- helpers (mirror PolylineDriverWalkDeleteGateTests) ----

        private static string MethodBody(string signatureFragment)
        {
            string src = StripComments(ReadParsekSource(RecorderPath));

            int sigIdx = src.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0,
                "SOI-seam gate: signature not found in " + RecorderPath + ": " + signatureFragment);
            Assert.True(
                src.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "SOI-seam gate: signature is ambiguous in " + RecorderPath + ": " + signatureFragment);

            int open = src.IndexOf('{', sigIdx);
            Assert.True(open >= 0, "SOI-seam gate: no method body found for " + signatureFragment);

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{')
                    depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return src.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "SOI-seam gate: unbalanced braces after " + signatureFragment);
            return null;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        // Strip line comments so the fence notes (which name the pre-fix symbols on purpose,
        // as pointers) do not trip the forbidden-token scans.
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
