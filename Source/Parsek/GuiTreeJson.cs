using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>One patched IMGUI funnel and how it fared, as reported into the JSON.</summary>
    internal sealed class GuiTreeFunnelReport
    {
        internal string Name;
        internal bool Patched;
        internal int Hits;

        internal GuiTreeFunnelReport(string name, bool patched, int hits)
        {
            Name = name;
            Patched = patched;
            Hits = hits;
        }
    }

    /// <summary>Everything the JSON writer needs that is not the tree itself.</summary>
    internal sealed class GuiTreeCaptureHeader
    {
        internal string Label = "capture";
        internal string CapturedUtc = string.Empty;
        internal int Frame;
        internal int ScreenWidth;
        internal int ScreenHeight;
        internal string ScreenshotHint;

        /// <summary>Patch-body exceptions swallowed during the captured frame.</summary>
        internal int RecordFaults;

        /// <summary>Events dropped because the per-frame cap was reached.</summary>
        internal int DroppedOverCap;

        /// <summary>
        /// <c>GUI.matrix</c> as read when the capture opened. It matters because
        /// <c>GUIUtility.GUIToScreenRect</c> converts only a rect's ORIGIN, so under a
        /// scaling matrix the recorder multiplies widths and heights through by hand - and
        /// a reader of the dump has to know that happened, and with what.
        /// </summary>
        internal bool MatrixIsIdentity = true;
        internal float MatrixM00 = 1f;
        internal float MatrixM11 = 1f;
        internal float MatrixM03;
        internal float MatrixM13;

        /// <summary>
        /// The GUI-state-gallery provenance, or null for an ordinary real-save capture.
        ///
        /// <para>It lives in the DUMP rather than in the filename because the owner must
        /// never have to remember which half of the mirror is real, and a filename
        /// convention is something a person can copy by hand. The mirror derives a
        /// capture's dataset from the lane's <c>fixture.saveTemplate</c>; a gallery lane
        /// HAS one (it needs a loaded game), so without this block its mocked captures
        /// would file under a real fixture's name and pair against real captures in
        /// Compare - the one lie that page must not tell.</para>
        /// </summary>
        internal GuiTreeMockProvenance Mock;

        internal readonly List<GuiTreeFunnelReport> Funnels = new List<GuiTreeFunnelReport>();
    }

    /// <summary>
    /// The <c>mock</c> block of a capture taken inside a GUI-state-gallery scope.
    ///
    /// <para>ADDITIVE: the schema id stays <c>parsek-gui-tree/1</c>. No key is renamed, no
    /// layout changes, and an older reader ignores an unknown object - the same
    /// additive-is-not-a-bump reasoning the recording schema uses for a new enum member.
    /// ABSENT means a real-save capture, which is the default and the common case.</para>
    /// </summary>
    internal sealed class GuiTreeMockProvenance
    {
        /// <summary>The catalogue state id, e.g. <c>kerbals.roster.lost</c>.</summary>
        internal string StateId;

        /// <summary>The window token the state drives.</summary>
        internal string Window;

        /// <summary>The catalogue contract token (<c>gui-mock/1</c>).</summary>
        internal string Catalogue;

        /// <summary>How many states the catalogue carried when this was captured, so a
        /// reader can tell a partial gallery from a whole one.</summary>
        internal int States;

        /// <summary>The branch keys the state claims, in declaration order.</summary>
        internal readonly List<string> Covers = new List<string>();
    }

    /// <summary>
    /// Hand-rolled JSON writer for the GUI-tree dump. Hand-rolled on purpose: the mod
    /// ships against net472 with no serializer dependency, and the output has to be
    /// byte-predictable for the offline viewer and its unit tests.
    ///
    /// <para>Every number goes through <see cref="CultureInfo.InvariantCulture"/>. This is
    /// the serialization case CLAUDE.md calls out: the xUnit host runs under the OS
    /// culture, so a bare <c>ToString()</c> would emit <c>12,5</c> on a ro-RO or de-DE
    /// machine and produce JSON no parser accepts.</para>
    /// </summary>
    internal static class GuiTreeJson
    {
        internal const string SchemaId = "parsek-gui-tree/1";

        internal static string Write(GuiTreeCaptureHeader header, GuiTreeResult tree)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(Str(SchemaId)).Append(",\n");
            sb.Append("  \"label\": ").Append(Str(header == null ? null : header.Label)).Append(",\n");
            sb.Append("  \"capturedUtc\": ").Append(Str(header == null ? null : header.CapturedUtc)).Append(",\n");
            sb.Append("  \"frame\": ").Append(Num(header == null ? 0 : header.Frame)).Append(",\n");
            sb.Append("  \"screen\": {\"width\": ")
              .Append(Num(header == null ? 0 : header.ScreenWidth))
              .Append(", \"height\": ")
              .Append(Num(header == null ? 0 : header.ScreenHeight))
              .Append("},\n");
            sb.Append("  \"screenshotHint\": ")
              .Append(Str(header == null ? null : header.ScreenshotHint)).Append(",\n");

            bool identity = header == null || header.MatrixIsIdentity;
            sb.Append("  \"guiMatrix\": {\"identity\": ").Append(identity ? "true" : "false");
            sb.Append(", \"m00\": ").Append(Num(header == null ? 1f : header.MatrixM00));
            sb.Append(", \"m11\": ").Append(Num(header == null ? 1f : header.MatrixM11));
            sb.Append(", \"m03\": ").Append(Num(header == null ? 0f : header.MatrixM03));
            sb.Append(", \"m13\": ").Append(Num(header == null ? 0f : header.MatrixM13));
            sb.Append("},\n");

            GuiTreeResult t = tree ?? new GuiTreeResult();
            sb.Append("  \"counts\": {");
            sb.Append("\"windows\": ").Append(Num(t.WindowCount));
            sb.Append(", \"nodes\": ").Append(Num(t.NodeCount));
            sb.Append(", \"events\": ").Append(Num(t.EventCount));
            sb.Append(", \"strayEnds\": ").Append(Num(t.StrayEnds));
            sb.Append(", \"autoClosedByClip\": ").Append(Num(t.AutoClosedByClip));
            sb.Append(", \"autoClosedByEnd\": ").Append(Num(t.AutoClosedByEnd));
            sb.Append(", \"autoClosedByRect\": ").Append(Num(t.AutoClosedByRect));
            sb.Append(", \"rectRuleInert\": ").Append(Num(t.RectRuleInert));
            sb.Append(", \"unclosedAtEnd\": ").Append(Num(t.UnclosedAtEnd));
            sb.Append(", \"recordFaults\": ").Append(Num(header == null ? 0 : header.RecordFaults));
            sb.Append(", \"droppedOverCap\": ").Append(Num(header == null ? 0 : header.DroppedOverCap));
            sb.Append("},\n");

            sb.Append("  \"funnels\": [");
            if (header != null && header.Funnels.Count > 0)
            {
                sb.Append('\n');
                for (int i = 0; i < header.Funnels.Count; i++)
                {
                    GuiTreeFunnelReport f = header.Funnels[i];
                    sb.Append("    {\"name\": ").Append(Str(f.Name));
                    sb.Append(", \"patched\": ").Append(f.Patched ? "true" : "false");
                    sb.Append(", \"hits\": ").Append(Num(f.Hits)).Append('}');
                    if (i < header.Funnels.Count - 1)
                        sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ");
            }
            sb.Append("],\n");

            // ADDITIVE and OMITTED when absent: an ordinary real-save capture writes no
            // `mock` key at all, so every existing dump byte-for-byte keeps its shape and
            // "absent means real" stays the reader's rule.
            if (header != null && header.Mock != null)
            {
                GuiTreeMockProvenance mock = header.Mock;
                sb.Append("  \"mock\": {\"stateId\": ").Append(Str(mock.StateId));
                sb.Append(", \"window\": ").Append(Str(mock.Window));
                sb.Append(", \"catalogue\": ").Append(Str(mock.Catalogue));
                sb.Append(", \"states\": ").Append(Num(mock.States));
                sb.Append(", \"covers\": [");
                for (int i = 0; i < mock.Covers.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Str(mock.Covers[i]));
                }
                sb.Append("]},\n");
            }

            sb.Append("  \"roots\": ");
            WriteNodes(sb, t.Roots, 1);
            sb.Append('\n');
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void WriteNodes(StringBuilder sb, List<GuiTreeNode> nodes, int depth)
        {
            if (nodes == null || nodes.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            string pad = new string(' ', depth * 2);
            string padInner = new string(' ', (depth + 1) * 2);
            sb.Append("[\n");
            for (int i = 0; i < nodes.Count; i++)
            {
                sb.Append(padInner);
                WriteNode(sb, nodes[i], depth + 1);
                if (i < nodes.Count - 1)
                    sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(pad).Append(']');
        }

        private static void WriteNode(StringBuilder sb, GuiTreeNode node, int depth)
        {
            sb.Append('{');
            sb.Append("\"kind\": ").Append(Str(GuiTreeAssembler.KindName(node.Kind)));
            sb.Append(", \"rect\": ").Append(RectJson(node.Rect));
            sb.Append(", \"localRect\": ").Append(RectJson(node.LocalRect));
            sb.Append(", \"clipDepth\": ").Append(Num(node.ClipDepth));
            sb.Append(", \"style\": ").Append(Str(node.StyleName));
            sb.Append(", \"enabled\": ").Append(node.Enabled ? "true" : "false");
            sb.Append(", \"text\": ").Append(Str(node.Text));
            if (node.Tooltip != null)
                sb.Append(", \"tooltip\": ").Append(Str(node.Tooltip));
            if (node.ToggleValue.HasValue)
                sb.Append(", \"value\": ").Append(node.ToggleValue.Value ? "true" : "false");
            if (node.TextValue != null)
                sb.Append(", \"textValue\": ").Append(Str(node.TextValue));
            if (node.ControlId.HasValue)
                sb.Append(", \"controlId\": ").Append(Num(node.ControlId.Value));
            if (node.WindowId.HasValue)
                sb.Append(", \"windowId\": ").Append(Num(node.WindowId.Value));
            if (node.SelectedIndex.HasValue)
                sb.Append(", \"selectedIndex\": ").Append(Num(node.SelectedIndex.Value));
            if (node.Horizontal.HasValue)
                sb.Append(", \"horizontal\": ").Append(node.Horizontal.Value ? "true" : "false");
            if (node.ContentOriginX.HasValue && node.ContentOriginY.HasValue)
            {
                sb.Append(", \"contentOrigin\": [")
                  .Append(Num(node.ContentOriginX.Value)).Append(", ")
                  .Append(Num(node.ContentOriginY.Value)).Append(']');
            }
            if (node.ArgWidth.HasValue && node.ArgHeight.HasValue)
            {
                sb.Append(", \"argSize\": [")
                  .Append(Num(node.ArgWidth.Value)).Append(", ")
                  .Append(Num(node.ArgHeight.Value)).Append(']');
            }
            sb.Append(", \"children\": ");
            WriteNodes(sb, node.Children, depth);
            sb.Append('}');
        }

        private static string RectJson(GuiRect r)
        {
            return "[" + Num(r.X) + ", " + Num(r.Y) + ", " + Num(r.W) + ", " + Num(r.H) + "]";
        }

        /// <summary>
        /// Invariant float formatting, rounded to 2 decimals. Rounded on purpose:
        /// a GUI rect is pixels, the viewer draws integer boxes, and full "R"
        /// round-tripping would triple the file for no reader.
        /// NaN / Infinity are not JSON numbers, so they become <c>null</c>.
        /// </summary>
        internal static string Num(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                return "null";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        internal static string Num(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// JSON string literal, or the <c>null</c> literal. Escapes the two mandatory
        /// characters, the C escapes, every remaining control character, and EVERY
        /// SURROGATE as <c>\uXXXX</c>; a stray control byte in a KSP part title would
        /// otherwise produce a file no parser accepts.
        ///
        /// <para><b>Why every surrogate, not just a lone one.</b> A UTF-16 string
        /// containing an unpaired surrogate - trivially produced by truncating a string
        /// with an emoji in it, which a UI label or a vessel name may well be - cannot be
        /// encoded as UTF-8 at all: <c>File.WriteAllText</c> substitutes U+FFFD or throws,
        /// and one bad character costs the ENTIRE dump. Escaping the whole surrogate range
        /// side-steps the question: a well-formed pair round-trips through its two
        /// <c>\uXXXX</c> escapes byte-for-byte (that is exactly how JSON encodes an astral
        /// character), and a lone one survives as an escape every parser accepts.</para>
        /// </summary>
        internal static string Str(string s)
        {
            if (s == null)
                return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == '\u007f' || char.IsSurrogate(c))
                        {
                            sb.Append("\\u")
                              .Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
