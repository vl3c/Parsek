using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// A minimal JSON reader for the sweep's own sidecars, so a later step can rebuild the
    /// manifest from every complete sidecar on disk (design 5.3: the label directory, not a
    /// static, carries the sweep across steps). Values map to
    /// <c>Dictionary&lt;string, object&gt;</c>, <c>List&lt;object&gt;</c>, <c>string</c>,
    /// <c>double</c>, <c>bool</c> and <c>null</c>. Pure; throws <see cref="FormatException"/>
    /// on malformed input (a torn sidecar is never complete: the writer is atomic).
    /// </summary>
    internal static class PartParityJsonReader
    {
        internal static object Parse(string text)
        {
            if (text == null) throw new FormatException("null text");
            int i = 0;
            object v = ReadValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i.ToString(CultureInfo.InvariantCulture));
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (Literal(s, ref i, "true")) return true;
            if (Literal(s, ref i, "false")) return false;
            if (Literal(s, ref i, "null")) return null;
            return ReadNumber(s, ref i);
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key");
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                d[key] = ReadValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        private static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("unexpected character at " + start.ToString(CultureInfo.InvariantCulture));
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ----- typed lookups (null when absent or of another type) -----

        private static object Get(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out object v) ? v : null;
        }

        internal static Dictionary<string, object> Obj(object o, string key) => Get(o, key) as Dictionary<string, object>;

        internal static List<object> Arr(object o, string key) => Get(o, key) as List<object>;

        internal static string Str(object o, string key) => Get(o, key) as string;

        internal static double? Num(object o, string key)
        {
            object v = Get(o, key);
            return v is double ? (double?)(double)v : null;
        }

        internal static int Int(object o, string key) => (int)Math.Round(Num(o, key) ?? 0.0);

        internal static bool Bool(object o, string key)
        {
            object v = Get(o, key);
            return v is bool && (bool)v;
        }

        /// <summary>
        /// Reads back the parts of a case sidecar the manifest and the control judge need:
        /// identity, setup, per-view measures and the structure lists (missing / extra).
        /// Never the pictures. Throws <see cref="FormatException"/> on a malformed file or a
        /// wrong schema id.
        /// </summary>
        internal static PartParityCaseSidecar ReadCaseSidecar(string text)
        {
            object root = Parse(text);
            if (Str(root, "schema") != PartParityJson.CaseSchemaId)
                throw new FormatException("not a " + PartParityJson.CaseSchemaId + " sidecar");
            var c = new PartParityCaseSidecar
            {
                CaseId = Str(root, "caseId"),
                Part = Str(root, "part"),
                Title = Str(root, "title"),
                Source = Str(root, "source"),
                State = Str(root, "state"),
                Context = Str(root, "context"),
            };
            var skin = Obj(root, "skin");
            if (skin != null)
                c.Skin = new PartParitySkinProof
                {
                    Module = Str(skin, "module"), ModuleId = Str(skin, "moduleId"),
                    Requested = Str(skin, "requested"), Real = Str(skin, "real"),
                    Snapshot = Str(skin, "snapshot"), Ghost = Str(skin, "ghost"),
                };
            var setup = Obj(root, "setup");
            if (setup != null)
                c.Setup = new PartParitySetup { Status = Str(setup, "status"), Detail = Str(setup, "detail") };
            var views = Arr(root, "views");
            if (views != null)
                foreach (object v in views) c.Views.Add(ReadView(v));
            var structure = Obj(root, "structure");
            if (structure != null) c.Structure = ReadStructure(structure);
            var flags = Arr(root, "flags");
            if (flags != null)
                foreach (object f in flags) if (f is string) c.Flags.Add((string)f);
            var proof = Obj(root, "stateProof");
            if (proof != null) c.StateProof = ReadStateProof(proof);
            var structureStart = Obj(root, "structureStart");
            if (structureStart != null) c.StructureStart = ReadStructure(structureStart);
            c.StowedCaseId = Str(root, "stowedCaseId");
            return c;
        }

        /// <summary>The parts of a <c>stateProof</c> block the manifest summary and the state
        /// control judge read (design D.2, D.6): what was pressed, the result, whether the state
        /// was reached, the wait, the recorded events and both ghost routes' outcomes.</summary>
        private static PartParityStateProof ReadStateProof(Dictionary<string, object> p)
        {
            var proof = new PartParityStateProof
            {
                Family = Str(p, "family"), Module = Str(p, "module"),
                ModuleIndex = Num(p, "moduleIndex").HasValue ? (int?)Int(p, "moduleIndex") : null,
                State = Str(p, "state"), Action = Str(p, "action"),
                ActionResult = Str(p, "actionResult"),
                Reached = Get(p, "reached") is bool r ? (bool?)r : null,
                WaitS = Num(p, "waitS"),
                GhostEvent = Str(p, "ghostEvent"), GhostStart = Str(p, "ghostStart"),
            };
            var endpoint = Obj(p, "endpoint");
            if (endpoint != null)
            {
                proof.EndpointField = Str(endpoint, "field");
                proof.EndpointValue = Str(endpoint, "value");
            }
            var events = Arr(p, "events");
            if (events != null)
            {
                proof.Events = new List<PartParityStateEvent>();
                foreach (object e in events)
                    proof.Events.Add(new PartParityStateEvent
                    {
                        Type = Str(e, "type"), Ut = Num(e, "ut"),
                        Pid = Num(e, "pid").HasValue ? (uint?)(uint)Math.Round(Num(e, "pid").Value) : null,
                        Midx = Num(e, "midx").HasValue ? (int?)Int(e, "midx") : null,
                    });
            }
            return proof;
        }

        private static PartParityViewRecord ReadView(object v)
        {
            PartParityViewRecord record = ReadMeasures(v);
            var start = Obj(v, "start");
            if (start != null)
            {
                PartParityViewRecord s = ReadMeasures(start);
                s.View = null;
                record.Start = s;
            }
            var stowedRef = Obj(v, "stowedRef");
            if (stowedRef != null) record.StowedRef = ReadMeasures(stowedRef).Measures;
            return record;
        }

        private static PartParityViewRecord ReadMeasures(object v)
        {
            var m = new PartParityViewMeasures
            {
                Width = Int(v, "width"), Height = Int(v, "height"),
                RealPx = Int(v, "realPx"), GhostPx = Int(v, "ghostPx"),
                AndPx = Int(v, "andPx"), OrPx = Int(v, "orPx"),
                RealOnlyPx = Int(v, "realOnlyPx"), GhostOnlyPx = Int(v, "ghostOnlyPx"),
                RealOnlyTolPx = Int(v, "realOnlyTolPx"), GhostOnlyTolPx = Int(v, "ghostOnlyTolPx"),
                RealOnlyBlobPx = Int(v, "realOnlyBlobPx"), GhostOnlyBlobPx = Int(v, "ghostOnlyBlobPx"),
                RealOnlyBlobPct = Num(v, "realOnlyBlobPct"), GhostOnlyBlobPct = Num(v, "ghostOnlyBlobPct"),
                ColorOverlapPx = Int(v, "colorOverlapPx"), ColorOverPx = Int(v, "colorOverPx"),
                ColorBlobPx = Int(v, "colorBlobPx"), ColorP99 = Num(v, "colorP99") ?? 0.0,
                ColorBlockMax = Num(v, "colorBlockMax") ?? 0.0,
                Iou = Num(v, "iou") ?? 1.0,
            };
            var tb = Obj(v, "touchesBorder");
            m.TouchesBorderReal = Bool(tb, "real");
            m.TouchesBorderGhost = Bool(tb, "ghost");
            return new PartParityViewRecord { View = Str(v, "view"), Measures = m };
        }

        private static PartParityStructureDiff ReadStructure(Dictionary<string, object> s)
        {
            var d = new PartParityStructureDiff
            {
                RealRenderers = Int(s, "realRenderers"),
                GhostRenderers = Int(s, "ghostRenderers"),
                Pairs = Int(s, "pairs"),
            };
            ReadRefs(Arr(s, "missingOnGhost"), d.MissingOnGhost);
            ReadRefs(Arr(s, "extraOnGhost"), d.ExtraOnGhost);
            return d;
        }

        private static void ReadRefs(List<object> items, List<PartParityRendererRef> into)
        {
            if (items == null) return;
            foreach (object o in items)
                into.Add(new PartParityRendererRef
                {
                    Key = Str(o, "key"), Mesh = Str(o, "mesh"), Rendered = Bool(o, "rendered"),
                    NonVisual = Str(o, "nonVisual"),
                });
        }
    }
}
