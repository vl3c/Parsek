using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.InGameTests.Spikes.S3
{
    // R0 spike branch only (spike/mp-r0, never merged). Spike S3, plan
    // docs/dev/plans/multiplayer/r0-spikes.md, "S3 The four looks, replacement sets, a
    // missing DLC, a suit on a ghost kerbal".
    //
    // The pure half of the spike: the stock suit tables read from the decompile, the
    // dress plans, the control file, the log line format and the dominant-color rule the
    // texture samples use. The four UI colors are JUDGED offline by
    // spikes/r0/S3/summarize_s3.py. No Unity or KSP call lives here (xUnit: SpikeS3Tests).

    /// <summary>One stock kerbal model: its suit type, gender, EVA part and IVA prefab.</summary>
    internal sealed class SpikeS3KerbalModel
    {
        internal readonly string Suit;
        internal readonly string Gender;
        internal readonly string EvaPart;
        internal readonly string IvaName;

        internal SpikeS3KerbalModel(string suit, string gender, string evaPart, string ivaName)
        {
            Suit = suit;
            Gender = gender;
            EvaPart = evaPart;
            IvaName = ivaName;
        }
    }

    /// <summary>One dressing step: the Nth kerbal of a roster pool gets a look.</summary>
    internal sealed class SpikeS3DressSlot
    {
        internal readonly string Pool;   // "crew" or "applicant"
        internal readonly int Index;     // position in that pool, roster order
        internal readonly string Look;   // a StockSuits or ParsekSets member

        internal SpikeS3DressSlot(string pool, int index, string look)
        {
            Pool = pool;
            Index = index;
            Look = look;
        }
    }

    internal static class SpikeS3Format
    {
        internal const string SpikeId = "S3";

        /// <summary>Log tag: [Parsek][INFO][Spike.S3].</summary>
        internal static string Tag => SpikeArming.SpikeTag(SpikeId);

        /// <summary>The control file the dress test reads: &lt;KSP root&gt;/parsek-spike/S3/dress.txt.</summary>
        internal const string ControlFileName = "dress.txt";

        /// <summary>Every SUITCOMBO name of the two test sets starts with this.</summary>
        internal const string ParsekSetPrefix = "ParsekS3_";

        /// <summary>The dress test saves the dressed game as &lt;prefix&gt;&lt;phase&gt;.sfs.</summary>
        internal const string PhaseSavePrefix = "s3-phase";

        /// <summary>Ghost builds per part for the timing (plan: "after 10 ghost builds").</summary>
        internal const int BuildsPerPart = 10;

        // ------------------------------------------------------------------ stock tables

        /// <summary>
        /// The four stock suit types, in ProtoCrewMember.KerbalSuit order (Default = 0,
        /// Vintage = 1, Future = 2, Slim = 3; decompiled KSP 1.12.5). SuitCombo.suitType and
        /// the picker compare against KerbalSuit.ToString(), so these spellings are the
        /// config values too.
        /// </summary>
        internal static readonly string[] StockSuits = { "Default", "Vintage", "Future", "Slim" };

        /// <summary>The design's look order (7.4): 0 base, 1 slim, 2 vintage, 3 future.</summary>
        internal static readonly string[] LookSuits = { "Default", "Slim", "Vintage", "Future" };

        /// <summary>The two replacement sets of the spike (spikes/r0/S3/gamedata).</summary>
        internal static readonly string[] ParsekSets = { "VintageLike", "FutureLike" };

        /// <summary>
        /// The eight kerbal models: ProtoCrewMember.GetKerbalEVAPartName() and
        /// GetKerbalIVAName() of KSP 1.12.5, by suit and gender. A Vintage or Future suit
        /// whose expansion is not installed answers the Default row's names.
        /// </summary>
        internal static readonly SpikeS3KerbalModel[] KerbalModels =
        {
            new SpikeS3KerbalModel("Default", "Male", "kerbalEVA", "kerbalMale"),
            new SpikeS3KerbalModel("Default", "Female", "kerbalEVAfemale", "kerbalFemale"),
            new SpikeS3KerbalModel("Vintage", "Male", "kerbalEVAVintage", "kerbalMaleVintage"),
            new SpikeS3KerbalModel("Vintage", "Female", "kerbalEVAfemaleVintage", "kerbalFemaleVintage"),
            new SpikeS3KerbalModel("Future", "Male", "kerbalEVAFuture", "kerbalMaleFuture"),
            new SpikeS3KerbalModel("Future", "Female", "kerbalEVAfemaleFuture", "kerbalFemaleFuture"),
            new SpikeS3KerbalModel("Slim", "Male", "kerbalEVASlimSuit", "slimSuitIVAMale"),
            new SpikeS3KerbalModel("Slim", "Female", "kerbalEVASlimSuitFemale", "slimSuitIVAFemale"),
        };

        /// <summary>
        /// The SUITCOMBO value keys SuitCombos.GetSuitCombos reads (decompiled; any other
        /// key is ignored). The node names are SUITCOMBOS (top level) and SUITCOMBO.
        /// </summary>
        internal static readonly string[] SuitComboKeys =
        {
            "name", "displayName", "suitType", "gender", "suitTexture", "normalTexture",
            "sprite", "primaryColor", "secondaryColor",
        };

        /// <summary>
        /// The look keys ProtoCrewMember.Save writes into a KERBAL node and its ConfigNode
        /// constructor reads back (decompiled). The pcm load hook logs them as they were
        /// on disk, before the getter resets an uninstalled suit.
        /// </summary>
        internal static readonly string[] PcmSaveKeys =
        {
            "suit", "suitTexturePath", "normalTexturePath", "spritePath", "comboId",
        };

        // ------------------------------------------------------------------ CSV headers

        internal static readonly string[] ComboHeader =
        {
            "session", "reason", "source", "index", "name", "displayName", "suitType", "gender",
            "suitTexture", "normalTexture", "sprite", "primaryColor", "secondaryColor",
            "defaultSuitTexture", "config",
        };

        internal static readonly string[] RosterHeader =
        {
            "session", "label", "scene", "name", "type", "status", "gender", "suit", "comboId",
            "suitTexturePath", "normalTexturePath", "spritePath", "evaPart", "evaFound", "iva",
            "ivaFound", "veteran", "aboard",
        };

        internal static readonly string[] ColorHeader =
        {
            "session", "look", "gender", "source", "texture", "dominant", "share", "chromatic",
            "chromaticShare", "primaryColor",
        };

        internal static readonly string[] GhostHeader =
        {
            "session", "part", "suit", "gender", "found", "fallback", "source", "builds",
            "buildMedianMs", "buildMaxMs", "swapMedianMs", "swapMaxMs", "buildSwapMedianMs",
            "materials", "renderers", "rolesMatched", "sharedBeforeSwap", "textureOk",
            "sharedUnchanged", "swapTexture",
        };

        /// <summary>
        /// The horizontal offset of item <paramref name="index"/> of a centred row of
        /// <paramref name="count"/> items <paramref name="spacing"/> apart.
        /// </summary>
        internal static double RowOffset(int index, int count, double spacing)
            => (index - (count - 1) / 2.0) * spacing;

        /// <summary>The model row of a suit and gender, or null for an unknown pair.</summary>
        internal static SpikeS3KerbalModel ModelFor(string suit, string gender)
        {
            foreach (SpikeS3KerbalModel m in KerbalModels)
                if (m.Suit == suit && m.Gender == gender) return m;
            return null;
        }

        /// <summary>
        /// The EVA part stock picks for a suit when that suit's expansion may be missing:
        /// Vintage / Future without their expansion fall back to the Default part, as
        /// GetKerbalEVAPartName does; Default and Slim are always "installed".
        /// </summary>
        internal static string EvaPartFor(string suit, string gender, bool vintageInstalled, bool futureInstalled)
        {
            string effective = suit;
            if (suit == "Vintage" && !vintageInstalled) effective = "Default";
            if (suit == "Future" && !futureInstalled) effective = "Default";
            SpikeS3KerbalModel m = ModelFor(effective, gender == "Female" ? "Female" : "Male");
            return m?.EvaPart;
        }

        // ------------------------------------------------------------------ looks and plans

        /// <summary>True for a look the spike's replacement sets provide.</summary>
        internal static bool IsParsekSet(string look)
            => Array.IndexOf(ParsekSets, look) >= 0;

        /// <summary>True for a look that is one of the four stock suit types.</summary>
        internal static bool IsStockSuit(string look)
            => Array.IndexOf(StockSuits, look) >= 0;

        /// <summary>The SUITCOMBO name of a replacement set for one gender.</summary>
        internal static string ParsekComboName(string set, string gender)
            => ParsekSetPrefix + set + "_" + gender;

        /// <summary>
        /// The dress plan of a phase. Phase 1 puts the three suits worth seeing on the
        /// first three crew (the three seats of the stock Kerbal X), the base suit on the
        /// fourth, and the two replacement sets on the first two applicants, so one saved
        /// game carries all six looks for the no-DLC load. Phase 2 puts the two sets and
        /// the base suit on the same three seats, for the IVA / EVA / portrait check of
        /// the sets. Any other phase: empty.
        /// </summary>
        internal static List<SpikeS3DressSlot> DressPlan(int phase)
        {
            var plan = new List<SpikeS3DressSlot>();
            if (phase == 1)
            {
                plan.Add(new SpikeS3DressSlot("crew", 0, "Vintage"));
                plan.Add(new SpikeS3DressSlot("crew", 1, "Future"));
                plan.Add(new SpikeS3DressSlot("crew", 2, "Slim"));
                plan.Add(new SpikeS3DressSlot("crew", 3, "Default"));
                plan.Add(new SpikeS3DressSlot("applicant", 0, "VintageLike"));
                plan.Add(new SpikeS3DressSlot("applicant", 1, "FutureLike"));
            }
            else if (phase == 2)
            {
                plan.Add(new SpikeS3DressSlot("crew", 0, "VintageLike"));
                plan.Add(new SpikeS3DressSlot("crew", 1, "FutureLike"));
                plan.Add(new SpikeS3DressSlot("crew", 2, "Default"));
            }
            return plan;
        }

        /// <summary>
        /// Parses the control file: one line <c>phase=&lt;1|2&gt;</c> (blank lines and
        /// lines starting with # ignored). Returns the phase, or 0 with the reason.
        /// </summary>
        internal static int ParseControl(string text, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(text)) { problem = "empty"; return 0; }
            int phase = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { problem = "not key=value: " + line; return 0; }
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key != "phase") { problem = "unknown key " + key; return 0; }
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out phase)
                    || DressPlan(phase).Count == 0)
                {
                    problem = "phase must be 1 or 2, got " + value;
                    return 0;
                }
            }
            if (phase == 0) problem = "no phase line";
            return phase;
        }

        // ------------------------------------------------------------------ log lines

        /// <summary>
        /// One measurement line body: "kind: k1=v1 k2=v2". Values are single tokens:
        /// whitespace becomes "_", null or empty becomes "-". Keys are written as given.
        /// </summary>
        internal static string Line(string kind, params string[] keyValues)
        {
            var sb = new StringBuilder();
            sb.Append(kind).Append(':');
            if (keyValues != null)
            {
                for (int i = 0; i + 1 < keyValues.Length; i += 2)
                    sb.Append(' ').Append(keyValues[i]).Append('=').Append(Token(keyValues[i + 1]));
            }
            return sb.ToString();
        }

        /// <summary>A single log token: whitespace to "_", null / empty to "-".</summary>
        internal static string Token(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsWhiteSpace(c) ? '_' : c);
            return sb.ToString();
        }

        internal static string B(bool v) => v ? "true" : "false";

        internal static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Milliseconds with three decimals, invariant ("0.125").</summary>
        internal static string Ms(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        internal static string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Median of a sample (0 for an empty one).</summary>
        internal static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            var s = new List<double>(values);
            s.Sort();
            int n = s.Count;
            return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
        }

        /// <summary>Largest value of a sample (0 for an empty one).</summary>
        internal static double Max(IList<double> values)
        {
            double m = 0;
            if (values == null) return m;
            for (int i = 0; i < values.Count; i++)
                if (i == 0 || values[i] > m) m = values[i];
            return m;
        }

        // ------------------------------------------------------------------ colors

        /// <summary>"#RRGGBB" (upper case) of three bytes.</summary>
        internal static string Hex(byte r, byte g, byte b)
            => "#" + r.ToString("X2", CultureInfo.InvariantCulture)
                   + g.ToString("X2", CultureInfo.InvariantCulture)
                   + b.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses "#RRGGBB", "RRGGBB", "#RGB" (case-insensitive). Unity's
        /// ColorUtility.TryParseHtmlString also takes "#RRGGBBAA"; the alpha is dropped
        /// here. False for anything else.
        /// </summary>
        internal static bool TryParseHex(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3)
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length == 8) s = s.Substring(0, 6);
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            r = (byte)((v >> 16) & 0xFF);
            g = (byte)((v >> 8) & 0xFF);
            b = (byte)(v & 0xFF);
            return true;
        }

        /// <summary>HSV hue in degrees [0, 360), saturation and value in 0..1.</summary>
        internal static void ToHsv(byte r, byte g, byte b, out double hue, out double sat, out double val)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double d = max - min;
            val = max;
            sat = max <= 0 ? 0 : d / max;
            if (d <= 0) { hue = 0; return; }
            double h;
            if (max == rf) h = (gf - bf) / d % 6.0;
            else if (max == gf) h = (bf - rf) / d + 2.0;
            else h = (rf - gf) / d + 4.0;
            h *= 60.0;
            if (h < 0) h += 360.0;
            hue = h >= 360.0 ? h - 360.0 : h;
        }

        /// <summary>
        /// The dominant color of an RGBA pixel buffer (4 bytes per pixel). Pixels with
        /// alpha under 128 or value (HSV max) under 0.12 are ignored (atlas padding and
        /// shadow). The colors are binned at 4 bits per channel; the answer is the mean
        /// of the fullest bin. With <paramref name="chromaticOnly"/>, pixels whose
        /// saturation is under 0.25 are ignored too, so a white suit with a colored trim
        /// answers the trim. Returns false when no pixel qualifies; share is the fraction
        /// of all pixels in the winning bin.
        /// </summary>
        internal static bool TryDominantColor(byte[] rgba, bool chromaticOnly,
            out byte r, out byte g, out byte b, out double share)
        {
            r = g = b = 0;
            share = 0;
            if (rgba == null || rgba.Length < 4) return false;
            int pixels = rgba.Length / 4;
            var count = new Dictionary<int, int>();
            var sumR = new Dictionary<int, long>();
            var sumG = new Dictionary<int, long>();
            var sumB = new Dictionary<int, long>();
            for (int p = 0; p < pixels; p++)
            {
                byte pr = rgba[p * 4], pg = rgba[p * 4 + 1], pb = rgba[p * 4 + 2], pa = rgba[p * 4 + 3];
                if (pa < 128) continue;
                ToHsv(pr, pg, pb, out double _, out double sat, out double val);
                if (val < 0.12) continue;
                if (chromaticOnly && sat < 0.25) continue;
                int key = ((pr >> 4) << 8) | ((pg >> 4) << 4) | (pb >> 4);
                count.TryGetValue(key, out int c);
                count[key] = c + 1;
                sumR.TryGetValue(key, out long sr); sumR[key] = sr + pr;
                sumG.TryGetValue(key, out long sg); sumG[key] = sg + pg;
                sumB.TryGetValue(key, out long sb); sumB[key] = sb + pb;
            }
            int best = -1, bestCount = 0;
            foreach (KeyValuePair<int, int> kv in count)
            {
                if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < best))
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            if (best < 0) return false;
            r = (byte)Math.Round(sumR[best] / (double)bestCount);
            g = (byte)Math.Round(sumG[best] / (double)bestCount);
            b = (byte)Math.Round(sumB[best] / (double)bestCount);
            share = bestCount / (double)pixels;
            return true;
        }
    }
}
