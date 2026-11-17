using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Parsek.InGameTests.Spikes;
using Parsek.InGameTests.Spikes.S3;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // Spike branch only (spike/mp-r0-s3). Pure parts of spike S3: the line format, the
    // control file, the dress plans, the color math and the dominant-color rule; the stock
    // tables checked against the reference Assembly-CSharp (string literals read from the
    // IL of the methods the tables transcribe); the committed test SUITCOMBOS sets. The
    // colors are JUDGED by spikes/r0/S3/summarize_s3.py (its --selftest), not here.
    public class SpikeS3Tests : IDisposable
    {
        private readonly CultureInfo savedCulture;

        public SpikeS3Tests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        }

        public void Dispose() => Thread.CurrentThread.CurrentCulture = savedCulture;

        private static string RepoRoot() =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));

        // ------------------------------------------------------------------ registry and lines

        [Fact]
        public void Registry_ListsS3BetweenS0AndS4a()
        {
            var ids = SpikeRegistry.Ids().ToList();
            int s0 = ids.IndexOf("S0"), s3 = ids.IndexOf("S3"), s4a = ids.IndexOf("S4a");
            Assert.True(s3 > s0 && s3 < s4a, string.Join(",", ids));
            Assert.Equal("Spike.S3", SpikeS3Format.Tag);
        }

        [Fact]
        public void Line_IsKindThenSingleTokenPairs_InvariantNumbers()
        {
            string line = SpikeS3Format.Line("ghost part", "part", "kerbalEVA", "ms",
                SpikeS3Format.Ms(1.5), "name", "Jebediah Kerman", "combo", null, "x", "");
            Assert.Equal("ghost part: part=kerbalEVA ms=1.500 name=Jebediah_Kerman combo=- x=-", line);
            Assert.Equal("0.125", SpikeS3Format.Ms(0.125));
            Assert.Equal("2.5", SpikeS3Format.D(2.5));
            Assert.Equal("true", SpikeS3Format.B(true));
            Assert.Equal("a_b_c", SpikeS3Format.Token("a b\tc"));
        }

        [Fact]
        public void Line_OddTrailingKeyIsDropped()
        {
            Assert.Equal("k: a=1", SpikeS3Format.Line("k", "a", "1", "orphan"));
            Assert.Equal("k:", SpikeS3Format.Line("k"));
        }

        // ------------------------------------------------------------------ control and plans

        [Theory]
        [InlineData("phase=1", 1)]
        [InlineData("# dress the roster\n\nphase = 2\r\n", 2)]
        public void ParseControl_ReadsThePhase(string text, int phase)
        {
            Assert.Equal(phase, SpikeS3Format.ParseControl(text, out string problem));
            Assert.Null(problem);
        }

        [Theory]
        [InlineData(null, "empty")]
        [InlineData("", "empty")]
        [InlineData("# only a comment", "no phase line")]
        [InlineData("phase=3", "phase must be 1 or 2")]
        [InlineData("phase=x", "phase must be 1 or 2")]
        [InlineData("suit=Vintage", "unknown key")]
        [InlineData("phase", "not key=value")]
        public void ParseControl_RefusesWithAReason(string text, string reason)
        {
            Assert.Equal(0, SpikeS3Format.ParseControl(text, out string problem));
            Assert.Contains(reason, problem);
        }

        [Fact]
        public void DressPlan_Phase1_ThreeSuitsOnTheFirstThreeSeats_SetsOnApplicants()
        {
            var plan = SpikeS3Format.DressPlan(1);
            Assert.Equal(new[] { "crew:0:Vintage", "crew:1:Future", "crew:2:Slim", "crew:3:Default",
                "applicant:0:VintageLike", "applicant:1:FutureLike" },
                plan.Select(s => s.Pool + ":" + s.Index + ":" + s.Look).ToArray());
            Assert.All(plan, s => Assert.True(SpikeS3Format.IsStockSuit(s.Look) || SpikeS3Format.IsParsekSet(s.Look)));
        }

        [Fact]
        public void DressPlan_Phase2_SetsOnTheFirstTwoSeats_OtherPhasesEmpty()
        {
            Assert.Equal(new[] { "crew:0:VintageLike", "crew:1:FutureLike", "crew:2:Default" },
                SpikeS3Format.DressPlan(2).Select(s => s.Pool + ":" + s.Index + ":" + s.Look).ToArray());
            Assert.Empty(SpikeS3Format.DressPlan(0));
            Assert.Empty(SpikeS3Format.DressPlan(3));
        }

        [Fact]
        public void Looks_AreTheFourStockSuitsInDesignOrder()
        {
            Assert.Equal(new[] { "Default", "Slim", "Vintage", "Future" }, SpikeS3Format.LookSuits);
            Assert.Equal(SpikeS3Format.StockSuits.OrderBy(s => s), SpikeS3Format.LookSuits.OrderBy(s => s));
            Assert.Equal("ParsekS3_VintageLike_Female", SpikeS3Format.ParsekComboName("VintageLike", "Female"));
        }

        [Theory]
        [InlineData("Vintage", "Male", true, true, "kerbalEVAVintage")]
        [InlineData("Vintage", "Male", false, true, "kerbalEVA")]
        [InlineData("Future", "Female", true, false, "kerbalEVAfemale")]
        [InlineData("Future", "Female", true, true, "kerbalEVAfemaleFuture")]
        [InlineData("Slim", "Female", false, false, "kerbalEVASlimSuitFemale")]
        [InlineData("Default", "Male", false, false, "kerbalEVA")]
        public void EvaPartFor_FallsBackLikeStock(string suit, string gender, bool vintage, bool future, string part)
        {
            Assert.Equal(part, SpikeS3Format.EvaPartFor(suit, gender, vintage, future));
        }

        [Fact]
        public void RowOffset_CentresTheRow()
        {
            Assert.Equal(-4.2, SpikeS3Format.RowOffset(0, 8, 1.2), 9);
            Assert.Equal(4.2, SpikeS3Format.RowOffset(7, 8, 1.2), 9);
            Assert.Equal(0.0, SpikeS3Format.RowOffset(0, 1, 1.5), 9);
        }

        [Fact]
        public void MedianAndMax()
        {
            Assert.Equal(2.0, SpikeS3Format.Median(new List<double> { 3, 1, 2 }));
            Assert.Equal(2.5, SpikeS3Format.Median(new List<double> { 4, 1, 2, 3 }));
            Assert.Equal(0.0, SpikeS3Format.Median(new List<double>()));
            Assert.Equal(-1.0, SpikeS3Format.Max(new List<double> { -3, -1, -2 }));
            Assert.Equal(0.0, SpikeS3Format.Max(null));
        }

        // ------------------------------------------------------------------ colors

        [Theory]
        [InlineData("#C9A227", 0xC9, 0xA2, 0x27)]
        [InlineData("c9a227", 0xC9, 0xA2, 0x27)]
        [InlineData("#fff", 255, 255, 255)]
        [InlineData("#11223380", 0x11, 0x22, 0x33)]
        public void TryParseHex_Accepts(string text, int r, int g, int b)
        {
            Assert.True(SpikeS3Format.TryParseHex(text, out byte rr, out byte gg, out byte bb));
            Assert.Equal(new[] { r, g, b }, new[] { (int)rr, gg, bb });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#12345")]
        [InlineData("#GG0000")]
        public void TryParseHex_Refuses(string text)
        {
            Assert.False(SpikeS3Format.TryParseHex(text, out _, out _, out _));
        }

        [Fact]
        public void Hsv_PrimariesAndGrey()
        {
            SpikeS3Format.ToHsv(255, 0, 0, out double h, out double s, out double v);
            Assert.Equal(0.0, h, 6); Assert.Equal(1.0, s, 6); Assert.Equal(1.0, v, 6);
            SpikeS3Format.ToHsv(0, 255, 0, out h, out _, out _);
            Assert.Equal(120.0, h, 6);
            SpikeS3Format.ToHsv(0, 0, 255, out h, out _, out _);
            Assert.Equal(240.0, h, 6);
            SpikeS3Format.ToHsv(255, 0, 128, out h, out _, out _);
            Assert.True(h > 329 && h < 331, h.ToString(CultureInfo.InvariantCulture));
            SpikeS3Format.ToHsv(128, 128, 128, out _, out s, out _);
            Assert.Equal(0.0, s, 9);
        }

        private static byte[] Pixels(params (byte r, byte g, byte b, byte a, int n)[] runs)
        {
            var list = new List<byte>();
            foreach (var run in runs)
                for (int i = 0; i < run.n; i++) list.AddRange(new[] { run.r, run.g, run.b, run.a });
            return list.ToArray();
        }

        [Fact]
        public void DominantColor_WhiteSuitWithRedTrim()
        {
            byte[] px = Pixels((250, 250, 250, 255, 60), (200, 20, 20, 255, 30), (0, 0, 0, 255, 50), (0, 255, 0, 0, 100));
            Assert.True(SpikeS3Format.TryDominantColor(px, false, out byte r, out byte g, out byte b, out double share));
            Assert.Equal("#FAFAFA", SpikeS3Format.Hex(r, g, b));
            Assert.Equal(60.0 / 240.0, share, 9);
            Assert.True(SpikeS3Format.TryDominantColor(px, true, out r, out g, out b, out share));
            Assert.Equal("#C81414", SpikeS3Format.Hex(r, g, b));
        }

        [Fact]
        public void DominantColor_NothingQualifies_IsFalse()
        {
            Assert.False(SpikeS3Format.TryDominantColor(null, false, out _, out _, out _, out _));
            Assert.False(SpikeS3Format.TryDominantColor(Pixels((0, 0, 0, 255, 10)), false, out _, out _, out _, out _));
            Assert.False(SpikeS3Format.TryDominantColor(Pixels((128, 128, 128, 255, 10)), true, out _, out _, out _, out _));
        }

        [Fact]
        public void DominantColor_MeansTheBinAndBreaksTiesByLowerBin()
        {
            byte[] px = Pixels((200, 0, 0, 255, 1), (206, 0, 0, 255, 1), (0, 0, 200, 255, 2));
            Assert.True(SpikeS3Format.TryDominantColor(px, false, out byte r, out byte g, out byte b, out _));
            Assert.Equal("#0000C8", SpikeS3Format.Hex(r, g, b)); // blue bin key < red bin key
            px = Pixels((200, 0, 0, 255, 2), (206, 0, 0, 255, 1));
            Assert.True(SpikeS3Format.TryDominantColor(px, false, out r, out g, out b, out _));
            Assert.Equal("#CA0000", SpikeS3Format.Hex(r, g, b));
        }

        [Fact]
        public void CsvHeaders_AreUnique()
        {
            foreach (string[] h in new[] { SpikeS3Format.ComboHeader, SpikeS3Format.RosterHeader,
                SpikeS3Format.ColorHeader, SpikeS3Format.GhostHeader })
                Assert.Equal(h.Length, h.Distinct().Count());
        }

        // ------------------------------------------------------------------ reference assembly

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null))
            .ToDictionary(o => o.Value, o => o);

        /// <summary>Every ldstr literal of a method body, decoded opcode by opcode.</summary>
        internal static List<string> Literals(MethodBase method)
        {
            var result = new List<string>();
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
                OpCode op = OpCodesByValue[value];
                i += op.Size;
                int operand;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: operand = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: operand = 1; break;
                    case OperandType.InlineVar: operand = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: operand = 8; break;
                    case OperandType.InlineSwitch: operand = 4 + 4 * BitConverter.ToInt32(il, i); break;
                    default: operand = 4; break;
                }
                if (op == OpCodes.Ldstr)
                    result.Add(method.Module.ResolveString(BitConverter.ToInt32(il, i)));
                i += operand;
            }
            return result;
        }

        private static MethodInfo Declared(Type t, string name, params Type[] args)
        {
            MethodInfo m = args.Length == 0 ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            Assert.True(m != null, t.Name + "." + name + " not found in the reference assembly");
            return m;
        }

        [Fact]
        public void KerbalModels_AreExactlyTheEvaAndIvaNamesStockReturns()
        {
            var eva = Literals(Declared(typeof(ProtoCrewMember), "GetKerbalEVAPartName"));
            var iva = Literals(Declared(typeof(ProtoCrewMember), "GetKerbalIVAName"));
            Assert.Equal(SpikeS3Format.KerbalModels.Select(m => m.EvaPart).OrderBy(s => s, StringComparer.Ordinal),
                eva.Distinct().OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal(SpikeS3Format.KerbalModels.Select(m => m.IvaName).OrderBy(s => s, StringComparer.Ordinal),
                iva.Distinct().OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal(8, SpikeS3Format.KerbalModels.Select(m => m.Suit + m.Gender).Distinct().Count());
        }

        [Fact]
        public void StockSuits_AreTheKerbalSuitEnumInOrder()
        {
            Assert.Equal(SpikeS3Format.StockSuits, Enum.GetNames(typeof(ProtoCrewMember.KerbalSuit)));
            Assert.Equal(new[] { "Male", "Female" }, Enum.GetNames(typeof(ProtoCrewMember.Gender)));
        }

        [Fact]
        public void SuitComboKeys_AreExactlyTheKeysGetSuitCombosReads_AndSuitComboFields()
        {
            var lits = Literals(Declared(typeof(SuitCombos), "GetSuitCombos"))
                .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var expected = SpikeS3Format.SuitComboKeys.Concat(new[] { "SUITCOMBO", "SUITCOMBOS" })
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, lits);
            foreach (string key in SpikeS3Format.SuitComboKeys)
            {
                FieldInfo f = typeof(SuitCombo).GetField(key, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(f != null && f.FieldType == typeof(string), "SuitCombo." + key);
            }
        }

        [Fact]
        public void PcmSaveKeys_AreWrittenBySave_AndTheLoadConstructorResolves()
        {
            var lits = Literals(Declared(typeof(ProtoCrewMember), "Save", typeof(ConfigNode)));
            foreach (string key in SpikeS3Format.PcmSaveKeys) Assert.Contains(key, lits);
            ConstructorInfo ctor = AccessTools.Constructor(typeof(ProtoCrewMember),
                new[] { typeof(Game.Modes), typeof(ConfigNode), typeof(ProtoCrewMember.KerbalType) });
            Assert.NotNull(ctor);
            Assert.Equal("node", ctor.GetParameters()[1].Name); // the postfix binds by this name
        }

        [Fact]
        public void LiveMembersTheSpikeTouches_ExistWithTheExpectedShape()
        {
            foreach (string f in new[] { "helmetMesh", "bodyMesh", "neckringMesh" })
                Assert.Equal(typeof(UnityEngine.SkinnedMeshRenderer), typeof(KerbalEVA).GetField(f).FieldType);
            foreach (string p in new[] { "SuitTexturePath", "NormalTexturePath", "SpritePath", "ComboId" })
            {
                PropertyInfo pi = typeof(ProtoCrewMember).GetProperty(p);
                Assert.True(pi != null && pi.CanWrite && pi.PropertyType == typeof(string), p);
            }
            Assert.NotNull(typeof(SuitCombos).GetMethod("GetCombo", new[] { typeof(string) }));
            Assert.NotNull(typeof(SuitCombos).GetProperty("ExtraCombos"));
        }

        // ------------------------------------------------------------------ committed sets

        private static List<Dictionary<string, string>> ReadCombos(string path)
        {
            var combos = new List<Dictionary<string, string>>();
            Dictionary<string, string> cur = null;
            string pending = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c).Trim();
                if (line.Length == 0) continue;
                if (line == "{") { if (pending == "SUITCOMBO") cur = new Dictionary<string, string>(); continue; }
                if (line == "}") { if (cur != null) { combos.Add(cur); cur = null; } continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) { pending = line; continue; }
                Assert.True(cur != null, "value outside a SUITCOMBO in " + path + ": " + line);
                cur.Add(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            }
            return combos;
        }

        [Fact]
        public void CommittedSets_UseOnlyKnownKeys_AndPointAtCommittedPngs()
        {
            string dir = Path.Combine(RepoRoot(), "spikes", "r0", "S3", "gamedata", "ParsekSpikeS3");
            string gameData = Path.GetDirectoryName(dir);
            var names = new List<string>();
            foreach (string set in SpikeS3Format.ParsekSets)
            {
                var combos = ReadCombos(Path.Combine(dir, set + ".cfg"));
                Assert.Equal(2, combos.Count);
                foreach (var combo in combos)
                {
                    Assert.Equal(SpikeS3Format.SuitComboKeys.OrderBy(k => k), combo.Keys.OrderBy(k => k));
                    Assert.Equal(SpikeS3Format.ParsekComboName(set, combo["gender"]), combo["name"]);
                    Assert.Contains(combo["suitType"], SpikeS3Format.StockSuits);
                    Assert.True(SpikeS3Format.TryParseHex(combo["primaryColor"], out _, out _, out _));
                    Assert.True(SpikeS3Format.TryParseHex(combo["secondaryColor"], out _, out _, out _));
                    foreach (string k in new[] { "suitTexture", "normalTexture", "sprite" })
                    {
                        string png = Path.Combine(gameData, combo[k].Replace('/', Path.DirectorySeparatorChar) + ".png");
                        Assert.True(File.Exists(png), png);
                        Assert.StartsWith("ParsekSpikeS3/", combo[k]);
                    }
                    names.Add(combo["name"]);
                }
            }
            Assert.Equal(new[] { "ParsekS3_FutureLike_Female", "ParsekS3_FutureLike_Male",
                "ParsekS3_VintageLike_Female", "ParsekS3_VintageLike_Male" }, names.OrderBy(n => n, StringComparer.Ordinal));
        }
    }
}
