using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gates for the route-line / ghost-polyline no-double-draw arbitration, in the
    /// <see cref="PolylineDriverWalkDeleteGateTests"/> shape and for the reason the 2026-09-06
    /// re-review (F1) found: the behavioural cells in <c>RouteMemberRunExpansionTests</c> drive a
    /// TEST-SIDE replica of the live loop (<c>ArbitrateGroup</c>) and a test-side paint seam
    /// (<c>SetLegPaintForTesting</c>, which calls the hide path itself), so the LIVE WIRING SITES
    /// the arbitration hangs off are invisible to them - delete one and all of those cells stay
    /// green while the shipped renderer either double-draws every route leg or stands the route
    /// line down forever.
    ///
    /// <para>The sites, and what each deletion costs:</para>
    /// <list type="bullet">
    /// <item><b>The hide path</b> - <c>ClearPaintedLegMesh</c> inside the Driver's
    /// <c>RunDeactivationSweep</c>. Mesh membership IS the paint fact; if hiding a leg does not
    /// retire its membership, the route line defers forever to a mesh that left the screen and that
    /// segment is never drawn again (the standing-condition mirror of the reading-run-2 defect).</item>
    /// <item><b>The rebuild path</b> - <c>ClearPaintedLegMesh</c> at <c>TryDrawLeg</c>'s map-line
    /// mode-flip rebuild (re-review F2). The rebuild DESTROYS the line, and the sweep cannot catch
    /// it because the sweep only flips lines that are currently active.</item>
    /// <item><b>The consumption</b> - both arbitration arms inside <c>DrawAll</c>. The ownership arm
    /// is per GROUP (it carries no UT span), the paint arm per LEG against that leg's own recorded
    /// span; a deleted arm is exactly the co-draw the M-A7 probe measured at 1024 on reading run 1.</item>
    /// </list>
    ///
    /// <para>The pins are STRUCTURAL rather than a file-wide grep: each is asserted inside the
    /// enclosing method's own brace-matched body, over a length-preserving sanitized copy of the
    /// source in which comments, string literals and char literals are blanked. A comment naming the
    /// call therefore cannot satisfy a pin (the trap a regex over raw source walks into), and
    /// neither can the same call made from some other method.</para>
    /// </summary>
    public class RouteLinePaintArbitrationSourceGateTests
    {
        private const string PolylinePath = "Display/GhostTrajectoryPolylineRenderer.cs";
        private const string RoutePath = "Display/RouteTrajectoryLineRenderer.cs";

        [Fact]
        public void DeactivationSweep_RetiresTheHiddenLegsPaintMembership()
        {
            string body = MethodBody(PolylinePath, "private int RunDeactivationSweep(int frame)");
            Assert.Contains("ClearPaintedLegMesh(kvp.Key, i);", body);
            // ... and it must sit on the hide path, beside the flip that hides the line.
            Assert.Contains("line.active = false;", body);
        }

        [Fact]
        public void LegLineRebuild_RetiresThePaintMembershipItJustDestroyed()
        {
            // Re-review F2. Scoped to the mode-flip rebuild block rather than to TryDrawLeg as a
            // whole, so a clear moved elsewhere in the method does not satisfy the pin.
            string body = MethodBody(PolylinePath, "internal static bool TryDrawLeg(");
            int flip = body.IndexOf("leg.lineMode != legWantMode", StringComparison.Ordinal);
            Assert.True(flip >= 0, "TryDrawLeg no longer carries the map-line mode-flip rebuild "
                                   + "guard - re-anchor this gate on whatever replaced it.");
            int rebuild = body.IndexOf("RebuildLineForMode(", flip, StringComparison.Ordinal);
            Assert.True(rebuild > flip, "TryDrawLeg's mode-flip guard no longer rebuilds the line.");
            Assert.Contains("ClearPaintedLegMesh(recordingId, legIndex);",
                            body.Substring(flip, rebuild - flip));
        }

        [Fact]
        public void DrawAll_ConsumesBothArbitrationArms()
        {
            string body = MethodBody(
                RoutePath,
                "internal static void DrawAll(int frame, int targetLayer, "
                + "Func<string, CelestialBody> resolveBody)");
            // OWNERSHIP arm, per group (it carries no UT span), counted in legs.
            Assert.Contains("ShouldSkipGroupAsGhostDrawn(group, ghostOwnsProbe)", body);
            Assert.Contains("ownedLegs += group.legs.Length;", body);
            // PAINT arm, per leg against that leg's own recorded span.
            Assert.Contains("ShouldSkipLegAsGhostPainted(", body);
            Assert.Contains("legs[i].startUT, legs[i].endUT,", body);
            Assert.Contains("paintedLegs++;", body);
        }

        // ---- helpers ----

        /// <summary>
        /// The brace-matched body of the method whose declaration starts with
        /// <paramref name="signature"/>, taken from a SANITIZED copy of the file (comments, string
        /// literals and char literals blanked, length preserved) so neither a comment nor a literal
        /// can satisfy a pin or unbalance the brace walk.
        /// </summary>
        private static string MethodBody(string relPath, string signature)
        {
            string sanitized = Sanitize(ReadParsekSource(relPath));
            int at = sanitized.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found in " + relPath + ": " + signature);
            Assert.True(sanitized.IndexOf(signature, at + 1, StringComparison.Ordinal) < 0,
                        "signature is not unique in " + relPath + ": " + signature);
            int open = sanitized.IndexOf('{', at + signature.Length);
            Assert.True(open >= 0, "no body brace after " + signature);
            int depth = 0;
            for (int i = open; i < sanitized.Length; i++)
            {
                if (sanitized[i] == '{') depth++;
                else if (sanitized[i] == '}')
                {
                    depth--;
                    if (depth == 0) return sanitized.Substring(open, i - open + 1);
                }
            }
            Assert.True(false, "unbalanced braces walking the body of " + signature);
            return null;
        }

        /// <summary>
        /// Length-preserving blanking of comments, string literals (plain and verbatim) and char
        /// literals: every such character becomes a space, so offsets stay valid and no brace inside
        /// a literal or a comment can move the walk.
        /// </summary>
        private static string Sanitize(string src)
        {
            var sb = new StringBuilder(src);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') { Blank(sb, i); i++; }
                }
                else if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    Blank(sb, i); Blank(sb, i + 1); i += 2;
                    while (i < src.Length
                           && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                    { Blank(sb, i); i++; }
                    if (i < src.Length) { Blank(sb, i); Blank(sb, i + 1); i += 2; }
                }
                else if (c == '@' && i + 1 < src.Length && src[i + 1] == '"')
                {
                    Blank(sb, i); Blank(sb, i + 1); i += 2;
                    while (i < src.Length)
                    {
                        if (src[i] == '"')
                        {
                            if (i + 1 < src.Length && src[i + 1] == '"')
                            { Blank(sb, i); Blank(sb, i + 1); i += 2; continue; }
                            Blank(sb, i); i++; break;
                        }
                        Blank(sb, i); i++;
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    char quote = c;
                    Blank(sb, i); i++;
                    while (i < src.Length && src[i] != quote)
                    {
                        if (src[i] == '\\' && i + 1 < src.Length)
                        { Blank(sb, i); Blank(sb, i + 1); i += 2; continue; }
                        Blank(sb, i); i++;
                    }
                    if (i < src.Length) { Blank(sb, i); i++; }
                }
                else i++;
            }
            return sb.ToString();
        }

        private static void Blank(StringBuilder sb, int index)
        {
            if (index < 0 || index >= sb.Length) return;
            if (sb[index] != '\n' && sb[index] != '\r') sb[index] = ' ';
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
    }
}
