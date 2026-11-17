using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S3
{
    // R0 spike branch only (spike/mp-r0, never merged). Spike S3: the four looks, two
    // replacement SUITCOMBOS sets, a save carried to a game without the DLC, and a suit
    // texture on a ghost kerbal.
    //
    // Armed by PARSEK_SPIKE=S3: SpikeRegistry calls Apply once at Startup.Instantly. Apply
    // hand-patches ONE target (no [HarmonyPatch] anywhere): a postfix on the ConfigNode
    // constructor of ProtoCrewMember, which logs what each saved kerbal's look keys were on
    // disk and what the loaded kerbal resolved to (the no-DLC question). It then creates one
    // DontDestroyOnLoad host that logs the suit combo inventory once at the first main menu.
    //
    // The in-game tests (category SpikeS3) do the rest; each skips when S3 is not armed.
    // Only ApplySuitToRoster changes the game: it dresses roster kerbals the way the stock
    // suit picker does and saves the result as s3-phase<N>.sfs in the loaded save folder.
    // Output: [Spike.S3] lines in KSP.log and CSV / PNG files under <KSP root>/parsek-spike/S3/.
    internal sealed class SpikeS3Looks : MonoBehaviour
    {
        internal static string Tag => SpikeS3Format.Tag;

        /// <summary>Process session stamp written in every CSV row (UTC, set at Apply).</summary>
        internal static string Session { get; private set; } = "unarmed";

        private static readonly HashSet<string> SeenPcmLines = new HashSet<string>();
        private static SpikeS3Looks instance;
        private bool inventoryLogged;
        private float nextPoll;

        // EVA kerbals seen in FLIGHT: key (vessel id, crew, suit, combo) -> first-seen time.
        // A kerbal is logged once, 2 s after it is first seen, so KerbalEVA.OnStart has
        // already put its textures on.
        private readonly Dictionary<string, float> evaSeen = new Dictionary<string, float>();
        private readonly HashSet<string> evaLogged = new HashSet<string>();
        private const float EvaSettleSeconds = 2f;

        // ------------------------------------------------------------------ apply

        /// <summary>SpikeRegistry entry: patches the kerbal load hook and creates the host.</summary>
        internal static void Apply(Harmony harmony)
        {
            Session = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            ParsekLog.Info(Tag, "looks probe armed session=" + Session + " dir=" + SafeDir());

            ConstructorInfo ctor = AccessTools.Constructor(typeof(ProtoCrewMember),
                new[] { typeof(Game.Modes), typeof(ConfigNode), typeof(ProtoCrewMember.KerbalType) });
            if (ctor == null)
            {
                ParsekLog.Warn(Tag, "hook missing: ProtoCrewMember(Game.Modes, ConfigNode, KerbalType)");
            }
            else
            {
                harmony.Patch(ctor, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SpikeS3Looks), nameof(PcmLoadPostfix))));
                ParsekLog.Info(Tag, "hook applied: ProtoCrewMember(Game.Modes, ConfigNode, KerbalType) postfix");
            }

            var go = new GameObject("ParsekSpikeS3Looks");
            DontDestroyOnLoad(go);
            go.AddComponent<SpikeS3Looks>();
        }

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 1f;
            try
            {
                if (!inventoryLogged && HighLogic.LoadedScene == GameScenes.MAINMENU)
                {
                    inventoryLogged = true;
                    LogExpansions();
                    LogInventory("mainmenu");
                }
                else if (HighLogic.LoadedSceneIsFlight)
                {
                    PollEva();
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "host poll failed: " + ex.Message);
            }
        }

        // Logs every EVA kerbal once, with what stock actually put on it.
        private void PollEva()
        {
            float now = Time.realtimeSinceStartup;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || !v.isEVA || v.rootPart == null) continue;
                ProtoCrewMember p = v.rootPart.protoModuleCrew.Count > 0 ? v.rootPart.protoModuleCrew[0] : null;
                string key = v.id + "|" + (p != null ? p.name + "|" + p.suit + "|" + p.ComboId : "-");
                if (evaLogged.Contains(key)) continue;
                float first;
                if (!evaSeen.TryGetValue(key, out first)) { evaSeen[key] = now; continue; }
                if (now - first < EvaSettleSeconds) continue;
                evaLogged.Add(key);
                LogEvaVessel("auto", v);
            }
        }

        // The constructor of a kerbal read from a save: stock parsed the node, reset an
        // uninstalled suit to Default, and kept comboId and the texture paths verbatim.
        private static void PcmLoadPostfix(ProtoCrewMember __instance, ConfigNode node)
        {
            try
            {
                if (__instance == null || node == null) return;
                string line = SpikeS3Format.Line("pcm load",
                    "name", __instance.name,
                    "type", __instance.type.ToString(),
                    "gender", __instance.gender.ToString(),
                    "nodeSuit", node.GetValue("suit"),
                    "nodeCombo", node.GetValue("comboId"),
                    "nodeSuitTexture", node.GetValue("suitTexturePath"),
                    "nodeSprite", node.GetValue("spritePath"),
                    "suit", __instance.suit.ToString(),
                    "comboId", __instance.ComboId,
                    "suitTexture", __instance.SuitTexturePath,
                    "evaPart", __instance.GetKerbalEVAPartName());
                if (SeenPcmLines.Add(line))
                    ParsekLog.Info(Tag, line);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "pcm load hook failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ inventory

        internal sealed class InventoryResult
        {
            internal int Default, Stock, Extra, ParsekSets, OtherExtra;
            internal bool Found;
        }

        internal static void LogExpansions()
        {
            ParsekLog.Info(Tag, SpikeS3Format.Line("expansions",
                "MakingHistory", SpikeS3Format.B(Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory")),
                "Serenity", SpikeS3Format.B(Expansions.ExpansionsLoader.IsExpansionInstalled("Serenity")),
                "suitVintage", SpikeS3Format.B(SuitInstalled(ProtoCrewMember.KerbalSuit.Vintage)),
                "suitFuture", SpikeS3Format.B(SuitInstalled(ProtoCrewMember.KerbalSuit.Future)),
                "suitSlim", SpikeS3Format.B(SuitInstalled(ProtoCrewMember.KerbalSuit.Slim))));
        }

        internal static bool SuitInstalled(ProtoCrewMember.KerbalSuit suit)
            => Expansions.ExpansionsLoader.IsExpansionKerbalSuitInstalled(suit);

        /// <summary>The stock SuitCombos component (it lives on the GameDatabase object).</summary>
        internal static SuitCombos FindCombos()
        {
            SuitCombos c = GameDatabase.Instance != null ? GameDatabase.Instance.GetComponent<SuitCombos>() : null;
            if (c == null) c = UnityEngine.Object.FindObjectOfType<SuitCombos>();
            return c;
        }

        /// <summary>SUITCOMBO name to the url of the config file that declared it.</summary>
        internal static Dictionary<string, string> ComboConfigUrls()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (GameDatabase.Instance == null) return map;
            foreach (UrlDir.UrlConfig uc in GameDatabase.Instance.GetConfigs("SUITCOMBOS"))
            {
                if (uc == null || uc.config == null) continue;
                string url = uc.parent != null ? uc.parent.url : uc.url;
                foreach (ConfigNode n in uc.config.GetNodes("SUITCOMBO"))
                {
                    string name = n.GetValue("name");
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name)) map[name] = url;
                }
            }
            return map;
        }

        /// <summary>Logs every combo (default, stock, extra) and one summary line.</summary>
        internal static InventoryResult LogInventory(string reason)
        {
            var r = new InventoryResult();
            SuitCombos combos = FindCombos();
            if (combos == null)
            {
                ParsekLog.Warn(Tag, "combos summary: found=false reason=" + reason);
                return r;
            }
            r.Found = true;
            Dictionary<string, string> urls = ComboConfigUrls();
            r.Default = LogComboList(combos.DefaultCombos, "default", reason, urls, null);
            r.Stock = LogComboList(combos.StockCombos, "stock", reason, urls, null);
            r.Extra = LogComboList(combos.ExtraCombos, "extra", reason, urls, r);
            ParsekLog.Info(Tag, SpikeS3Format.Line("combos summary",
                "reason", reason,
                "default", SpikeS3Format.I(r.Default),
                "stock", SpikeS3Format.I(r.Stock),
                "extra", SpikeS3Format.I(r.Extra),
                "parsekSets", SpikeS3Format.I(r.ParsekSets),
                "otherExtra", SpikeS3Format.I(r.OtherExtra),
                "configs", SpikeS3Format.I(GameDatabase.Instance.GetConfigs("SUITCOMBOS").Length)));
            return r;
        }

        private static int LogComboList(List<SuitCombo> list, string source, string reason,
            Dictionary<string, string> urls, InventoryResult counts)
        {
            if (list == null)
            {
                ParsekLog.Info(Tag, SpikeS3Format.Line("combo list", "source", source, "state", "null"));
                return 0;
            }
            for (int i = 0; i < list.Count; i++)
            {
                SuitCombo c = list[i];
                if (c == null) continue;
                string url;
                if (c.name == null || !urls.TryGetValue(c.name, out url))
                    url = source == "extra" ? "-" : "prefab";
                if (counts != null)
                {
                    if (c.name != null && c.name.StartsWith(SpikeS3Format.ParsekSetPrefix, StringComparison.Ordinal))
                        counts.ParsekSets++;
                    else
                        counts.OtherExtra++;
                }
                string defTex = TextureName(c.defaultSuitTexture);
                ParsekLog.Info(Tag, SpikeS3Format.Line("combo",
                    "source", source, "index", SpikeS3Format.I(i),
                    "name", c.name, "displayName", c.displayName,
                    "suitType", c.suitType, "gender", c.gender,
                    "suitTexture", c.suitTexture, "normalTexture", c.normalTexture,
                    "sprite", c.sprite, "primary", c.primaryColor, "secondary", c.secondaryColor,
                    "defaultTex", defTex, "config", url));
                SafeCsv("combos", SpikeS3Format.ComboHeader, Session, reason, source, i, c.name,
                    c.displayName, c.suitType, c.gender, c.suitTexture, c.normalTexture, c.sprite,
                    c.primaryColor, c.secondaryColor, defTex, url);
            }
            return list.Count;
        }

        /// <summary>First combo of a list with this suit type and gender, or null.</summary>
        internal static SuitCombo FirstCombo(List<SuitCombo> list, string suitType, string gender)
        {
            if (list == null) return null;
            foreach (SuitCombo c in list)
                if (c != null && c.suitType == suitType && c.gender == gender) return c;
            return null;
        }

        // ------------------------------------------------------------------ roster

        /// <summary>One line and one CSV row per crew and applicant kerbal; returns the count.</summary>
        internal static int LogRoster(string label)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.CrewRoster == null)
            {
                ParsekLog.Info(Tag, SpikeS3Format.Line("roster", "label", label, "state", "no-game"));
                return 0;
            }
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            var aboard = new HashSet<string>(StringComparer.Ordinal);
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                foreach (ProtoCrewMember p in FlightGlobals.ActiveVessel.GetVesselCrew())
                    if (p != null) aboard.Add(p.name);

            int n = 0;
            foreach (ProtoCrewMember p in roster.Crew) { LogKerbal(label, p, aboard); n++; }
            foreach (ProtoCrewMember p in roster.Applicants) { LogKerbal(label, p, aboard); n++; }
            ParsekLog.Info(Tag, SpikeS3Format.Line("roster summary", "label", label,
                "scene", HighLogic.LoadedScene.ToString(), "kerbals", SpikeS3Format.I(n),
                "aboard", SpikeS3Format.I(aboard.Count)));
            if (HighLogic.LoadedSceneIsFlight) LogLiveEva(label);
            return n;
        }

        private static void LogKerbal(string label, ProtoCrewMember p, HashSet<string> aboard)
        {
            if (p == null) return;
            string eva = p.GetKerbalEVAPartName();
            bool evaFound = PartLoader.getPartInfoByName(eva) != null;
            string iva = p.GetKerbalIVAName();
            bool ivaFound = AssetBase.GetPrefab(iva) != null;
            bool isAboard = aboard.Contains(p.name);
            ParsekLog.Info(Tag, SpikeS3Format.Line("roster",
                "label", label, "name", p.name, "type", p.type.ToString(),
                "status", p.rosterStatus.ToString(), "gender", p.gender.ToString(),
                "suit", p.suit.ToString(), "comboId", p.ComboId,
                "suitTexture", p.SuitTexturePath, "normalTexture", p.NormalTexturePath,
                "sprite", p.SpritePath, "evaPart", eva, "evaFound", SpikeS3Format.B(evaFound),
                "iva", iva, "ivaFound", SpikeS3Format.B(ivaFound),
                "veteran", SpikeS3Format.B(p.veteran), "aboard", SpikeS3Format.B(isAboard)));
            SafeCsv("roster", SpikeS3Format.RosterHeader, Session, label, HighLogic.LoadedScene.ToString(),
                p.name, p.type.ToString(), p.rosterStatus.ToString(), p.gender.ToString(), p.suit.ToString(),
                p.ComboId, p.SuitTexturePath, p.NormalTexturePath, p.SpritePath, eva, evaFound, iva,
                ivaFound, p.veteran, isAboard);
        }

        // What stock actually put on each loaded EVA kerbal (instance materials after OnStart).
        private static void LogLiveEva(string label)
        {
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
                if (v != null && v.isEVA && v.rootPart != null) LogEvaVessel(label, v);
        }

        private static void LogEvaVessel(string label, Vessel v)
        {
            KerbalEVA kev = v.rootPart.FindModuleImplementing<KerbalEVA>();
            ProtoCrewMember p = v.rootPart.protoModuleCrew.Count > 0 ? v.rootPart.protoModuleCrew[0] : null;
            ParsekLog.Info(Tag, SpikeS3Format.Line("eva live",
                "label", label, "vessel", v.vesselName,
                "part", v.rootPart.partInfo != null ? v.rootPart.partInfo.name : null,
                "crew", p != null ? p.name : null,
                "suit", p != null ? p.suit.ToString() : null,
                "comboId", p != null ? p.ComboId : null,
                "suitTexture", p != null ? p.SuitTexturePath : null,
                "bodyTex", kev != null ? RendererTexture(kev.bodyMesh) : null,
                "helmetTex", kev != null ? RendererTexture(kev.helmetMesh) : null,
                "neckTex", kev != null ? RendererTexture(kev.neckringMesh) : null));
        }

        private static string RendererTexture(Renderer r)
        {
            if (r == null || r.sharedMaterial == null) return null;
            return TextureName(r.sharedMaterial.mainTexture);
        }

        // ------------------------------------------------------------------ dressing

        internal sealed class DressOutcome
        {
            internal bool Ok;
            internal string Reason;
        }

        /// <summary>
        /// Dresses one kerbal the way HelmetSuitPickerWindow does (decompiled): a stock
        /// look sets the suit, clears the three texture paths and sets the stock comboId;
        /// a replacement set sets the combo's suit type, its three paths and its name.
        /// </summary>
        internal static DressOutcome Dress(ProtoCrewMember p, string look, SuitCombos combos)
        {
            var o = new DressOutcome { Ok = true, Reason = "-" };
            string gender = p.gender.ToString();
            if (SpikeS3Format.IsStockSuit(look))
            {
                var suit = (ProtoCrewMember.KerbalSuit)Enum.Parse(typeof(ProtoCrewMember.KerbalSuit), look);
                SuitCombo sc = FirstCombo(combos != null ? combos.StockCombos : null, look, gender);
                p.suit = suit;
                p.SuitTexturePath = null;
                p.NormalTexturePath = null;
                p.SpritePath = null;
                p.ComboId = sc != null ? sc.name : null;
                if (p.suit != suit) { o.Ok = false; o.Reason = "suit-refused"; }
                else if (sc == null) { o.Reason = "no-stock-combo"; }
                return o;
            }
            if (SpikeS3Format.IsParsekSet(look))
            {
                SuitCombo c = combos != null ? combos.GetCombo(SpikeS3Format.ParsekComboName(look, gender)) : null;
                if (c == null) { o.Ok = false; o.Reason = "combo-missing"; return o; }
                ProtoCrewMember.KerbalSuit suit;
                try { suit = (ProtoCrewMember.KerbalSuit)Enum.Parse(typeof(ProtoCrewMember.KerbalSuit), c.suitType); }
                catch (Exception) { o.Ok = false; o.Reason = "bad-suitType"; return o; }
                p.suit = suit;
                p.SuitTexturePath = c.suitTexture;
                p.NormalTexturePath = c.normalTexture;
                p.SpritePath = c.sprite;
                p.ComboId = c.name;
                if (p.suit != suit) { o.Ok = false; o.Reason = "suit-refused"; }
                return o;
            }
            o.Ok = false;
            o.Reason = "unknown-look";
            return o;
        }

        internal static string ControlPath()
            => Path.Combine(Path.Combine(SpikeCsv.ResolveSpikeRoot(), SpikeS3Format.SpikeId), SpikeS3Format.ControlFileName);

        // ------------------------------------------------------------------ colors

        /// <summary>
        /// Samples one texture: blits it to a small readable target and asks the pure
        /// dominant-color rule (all pixels, then chromatic pixels only).
        /// </summary>
        internal static void SampleTexture(string look, string gender, string source, Texture tex, string primary)
        {
            string dom = "-", chrom = "-", share = "-", cshare = "-";
            if (tex != null)
            {
                byte[] rgba = ReadPixelsRgba(tex, 128);
                if (rgba != null)
                {
                    if (SpikeS3Format.TryDominantColor(rgba, false, out byte r, out byte g, out byte b, out double s))
                    {
                        dom = SpikeS3Format.Hex(r, g, b);
                        share = SpikeS3Format.D(s);
                    }
                    if (SpikeS3Format.TryDominantColor(rgba, true, out r, out g, out b, out s))
                    {
                        chrom = SpikeS3Format.Hex(r, g, b);
                        cshare = SpikeS3Format.D(s);
                    }
                }
            }
            string texName = TextureName(tex);
            ParsekLog.Info(Tag, SpikeS3Format.Line("color sample",
                "look", look, "gender", gender, "source", source, "texture", texName,
                "dominant", dom, "share", share, "chromatic", chrom, "chromaticShare", cshare,
                "primaryColor", primary));
            SafeCsv("colors", SpikeS3Format.ColorHeader, Session, look, gender, source, texName,
                dom, share, chrom, cshare, primary);
        }

        // Unity: kept out of every method a test calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static byte[] ReadPixelsRgba(Texture tex, int size)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(size, size, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                copy.Apply();
                Color32[] px = copy.GetPixels32();
                var bytes = new byte[px.Length * 4];
                for (int i = 0; i < px.Length; i++)
                {
                    bytes[i * 4] = px[i].r;
                    bytes[i * 4 + 1] = px[i].g;
                    bytes[i * 4 + 2] = px[i].b;
                    bytes[i * 4 + 3] = px[i].a;
                }
                return bytes;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "texture read failed: " + TextureName(tex) + " " + ex.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        /// <summary>The prefab body texture of an EVA part, or null when the part is missing.</summary>
        internal static Texture PrefabBodyTexture(string evaPart)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(evaPart);
            if (ap == null || ap.partPrefab == null) return null;
            KerbalEVA kev = ap.partPrefab.FindModuleImplementing<KerbalEVA>();
            if (kev == null || kev.bodyMesh == null || kev.bodyMesh.sharedMaterial == null) return null;
            return kev.bodyMesh.sharedMaterial.mainTexture;
        }

        // ------------------------------------------------------------------ helpers

        internal static string TextureName(Texture t)
        {
            if (t == null) return null;
            string n = string.IsNullOrEmpty(t.name) ? "unnamed" : t.name;
            return n + "#" + t.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        internal static void SafeCsv(string name, string[] header, params object[] fields)
        {
            try { SpikeCsv.AppendRow(SpikeS3Format.SpikeId, name, header, fields); }
            catch (Exception ex) { ParsekLog.Warn(Tag, "csv " + name + " write failed: " + ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SafeDir()
        {
            try { return Path.Combine(SpikeCsv.ResolveSpikeRoot(), SpikeS3Format.SpikeId); }
            catch (Exception ex) { return "<unresolved: " + ex.Message + ">"; }
        }
    }

    /// <summary>The in-game entry points the plan names (category SpikeS3).</summary>
    // AllowBatchExecution stays true (the default) on purpose: the command seam's
    // RunTests verb runs only batch-eligible tests, and the PC sheet drives these through
    // it. The batch's isolation (persistent.sfs .bak at the Space Center, a quickload of
    // the batch-start baseline in flight) undoes the dressing in memory or on disk, which
    // is why the dress test writes its own s3-phase<N> save and the sheet loads it.
    public class SpikeS3LooksTests
    {
        private const string Category = "SpikeS3";

        // The ghost test has its own category so the sheet can run the roster tests in
        // flight without paying for 160 ghost builds and two screenshots each time (the
        // seam's RunTests selects by category only).
        private const string GhostCategory = "SpikeS3Ghost";

        private static bool Armed() => SpikeHost.IsArmed(SpikeS3Format.SpikeId);

        [InGameTest(Category = Category,
            Description = "Spike S3: log every suit combo (default, stock, extra, source config) and "
                + "sample the suit colors of the four looks and the two test sets (needs PARSEK_SPIKE=S3)")]
        public void ListSuitCombos()
        {
            if (!Armed()) { InGameAssert.Skip("PARSEK_SPIKE does not name S3"); return; }
            SpikeS3Looks.LogExpansions();
            SpikeS3Looks.InventoryResult inv = SpikeS3Looks.LogInventory("test");
            InGameAssert.IsTrue(inv.Found, "SuitCombos component not found");
            SuitCombos combos = SpikeS3Looks.FindCombos();

            foreach (string gender in new[] { "Male", "Female" })
            {
                foreach (string look in SpikeS3Format.LookSuits)
                {
                    SpikeS3KerbalModel m = SpikeS3Format.ModelFor(look, gender);
                    SuitCombo sc = SpikeS3Looks.FirstCombo(combos.StockCombos, look, gender);
                    SpikeS3Looks.SampleTexture(look, gender, "prefab-body:" + m.EvaPart,
                        SpikeS3Looks.PrefabBodyTexture(m.EvaPart), sc != null ? sc.primaryColor : null);
                }
                foreach (string set in SpikeS3Format.ParsekSets)
                {
                    SuitCombo c = combos.GetCombo(SpikeS3Format.ParsekComboName(set, gender));
                    Texture tex = c != null ? GameDatabase.Instance.GetTexture(c.suitTexture, false) : null;
                    SpikeS3Looks.SampleTexture(set, gender, "set:" + (c != null ? c.suitTexture : "missing"),
                        tex, c != null ? c.primaryColor : null);
                }
            }
            // Every stock combo that carries its own texture (the picker's color variants).
            for (int i = 0; combos.StockCombos != null && i < combos.StockCombos.Count; i++)
            {
                SuitCombo c = combos.StockCombos[i];
                if (c == null || c.defaultSuitTexture == null) continue;
                SpikeS3Looks.SampleTexture(c.suitType, c.gender, "stock-combo:" + c.name, c.defaultSuitTexture, c.primaryColor);
            }
        }

        [InGameTest(Category = Category,
            Description = "Spike S3: log every crew and applicant kerbal's suit, combo, texture paths, "
                + "EVA part and IVA prefab, and in flight the loaded EVA kerbals' textures (needs PARSEK_SPIKE=S3)")]
        public void LogRoster()
        {
            if (!Armed()) { InGameAssert.Skip("PARSEK_SPIKE does not name S3"); return; }
            int n = SpikeS3Looks.LogRoster("test-" + HighLogic.LoadedScene);
            InGameAssert.IsTrue(n > 0, "no roster kerbal logged");
        }

        [InGameTest(Category = Category,
            Description = "Spike S3: dress roster kerbals per parsek-spike/S3/dress.txt (phase=1|2) the "
                + "way the stock suit picker does, then save s3-phase<N> (needs PARSEK_SPIKE=S3)")]
        public void ApplySuitToRoster()
        {
            if (!Armed()) { InGameAssert.Skip("PARSEK_SPIKE does not name S3"); return; }
            string path = SpikeS3Looks.ControlPath();
            if (!File.Exists(path)) { InGameAssert.Skip("no control file " + path); return; }
            int phase = SpikeS3Format.ParseControl(File.ReadAllText(path), out string problem);
            if (phase == 0) { InGameAssert.Fail("control file " + path + ": " + problem); return; }

            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            SuitCombos combos = SpikeS3Looks.FindCombos();
            var crew = new List<ProtoCrewMember>(roster.Crew);
            var applicants = new List<ProtoCrewMember>(roster.Applicants);
            int failed = 0;
            foreach (SpikeS3DressSlot slot in SpikeS3Format.DressPlan(phase))
            {
                List<ProtoCrewMember> pool = slot.Pool == "applicant" ? applicants : crew;
                while (slot.Pool == "applicant" && pool.Count <= slot.Index)
                {
                    ProtoCrewMember made = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Applicant);
                    pool.Add(made);
                    ParsekLog.Info(SpikeS3Looks.Tag, SpikeS3Format.Line("applicant created",
                        "name", made != null ? made.name : null));
                }
                if (pool.Count <= slot.Index || pool[slot.Index] == null)
                {
                    failed++;
                    ParsekLog.Warn(SpikeS3Looks.Tag, SpikeS3Format.Line("dress", "phase", SpikeS3Format.I(phase),
                        "pool", slot.Pool, "index", SpikeS3Format.I(slot.Index), "look", slot.Look,
                        "ok", "false", "reason", "no-kerbal"));
                    continue;
                }
                ProtoCrewMember p = pool[slot.Index];
                string before = p.suit + "/" + (p.ComboId ?? "-");
                SpikeS3Looks.DressOutcome o = SpikeS3Looks.Dress(p, slot.Look, combos);
                if (!o.Ok) failed++;
                ParsekLog.Info(SpikeS3Looks.Tag, SpikeS3Format.Line("dress",
                    "phase", SpikeS3Format.I(phase), "pool", slot.Pool, "index", SpikeS3Format.I(slot.Index),
                    "name", p.name, "gender", p.gender.ToString(), "look", slot.Look,
                    "before", before, "suit", p.suit.ToString(), "comboId", p.ComboId,
                    "suitTexture", p.SuitTexturePath, "sprite", p.SpritePath,
                    "ok", SpikeS3Format.B(o.Ok), "reason", o.Reason));
            }
            SpikeS3Looks.LogRoster("dressed-phase" + phase);
            string saveName = SpikeS3Format.PhaseSavePrefix + phase.ToString(CultureInfo.InvariantCulture);
            string result = GamePersistence.SaveGame(saveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            ParsekLog.Info(SpikeS3Looks.Tag, SpikeS3Format.Line("saved",
                "name", saveName, "folder", HighLogic.SaveFolder, "result", result));
            InGameAssert.IsTrue(failed == 0, failed + " dress step(s) failed (see [Spike.S3] dress lines)");
            InGameAssert.IsFalse(string.IsNullOrEmpty(result), "SaveGame returned nothing for " + saveName);
        }

        [InGameTest(Category = GhostCategory, Scene = GameScenes.FLIGHT,
            Description = "Spike S3: build the eight EVA kerbal ghosts through GhostVisualBuilder, time the "
                + "per-ghost material swap, check the prefab shared material, show and photograph them "
                + "(needs PARSEK_SPIKE=S3)")]
        public IEnumerator BuildGhostKerbal()
        {
            if (!Armed()) { InGameAssert.Skip("PARSEK_SPIKE does not name S3"); yield break; }
            var run = new SpikeS3Ghosts.Run();
            int census0 = SpikeS3Ghosts.MaterialCensus();
            foreach (SpikeS3KerbalModel m in SpikeS3Format.KerbalModels)
            {
                SpikeS3Ghosts.MeasurePart(run, m);
                yield return null;
            }
            yield return null;
            yield return null;
            int census1 = SpikeS3Ghosts.MaterialCensus();

            IEnumerator show = SpikeS3Ghosts.ShowAndPhotograph(run);
            while (show.MoveNext()) yield return show.Current;
            yield return null;
            yield return null;
            int census2 = SpikeS3Ghosts.MaterialCensus();
            // measured: after the 160 measurement builds (the eight row-A ghosts still alive,
            // sharing the prefab materials); final: after the display was destroyed.
            ParsekLog.Info(SpikeS3Looks.Tag, SpikeS3Format.Line("material census",
                "before", SpikeS3Format.I(census0), "measured", SpikeS3Format.I(census1),
                "final", SpikeS3Format.I(census2), "deltaFinal", SpikeS3Format.I(census2 - census0)));

            ParsekLog.Info(SpikeS3Looks.Tag, SpikeS3Format.Line("ghost summary",
                "parts", SpikeS3Format.I(run.Parts), "found", SpikeS3Format.I(run.Found),
                "fallbacks", SpikeS3Format.I(run.Fallbacks),
                "sharedChanged", SpikeS3Format.I(run.SharedChanged),
                "textureBad", SpikeS3Format.I(run.TextureBad)));
            InGameAssert.IsTrue(run.Found > 0, "no EVA part resolved");
            InGameAssert.IsTrue(run.SharedChanged == 0, run.SharedChanged + " prefab shared material(s) changed");
            InGameAssert.IsTrue(run.TextureBad == 0, run.TextureBad + " part(s) did not take the swapped texture");
        }
    }
}
