using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6 step (f).
    //
    // The race content table's raw input. At the main menu (PARSEK_SPIKE_S6_CONTENT=1) one cfg
    // file, parsek-spike/S6/content-<label>-mainmenu.cfg, holds: the GameData top-level
    // folders; the game database's TechTree nodes as canonical lines (the read
    // RDTechTree.LoadTechTree makes with loadFromDatabase, through GetTechNodesFromDB); the
    // stock file GameData/Squad/Resources/TechTree.cfg read raw; every loaded part with its
    // partConfig as ModuleManager left it; every PARTUPGRADE and EXPERIMENT_DEFINITION node;
    // every body's scienceValues. In the Space Center the R&D tree
    // (AssetBase.RnDTechTree.GetTreeNodes()) is written the same way and compared with the
    // main-menu read. spikes/r0/S6/content_diff.py compares two dumps.
    internal static class SpikeS6ContentDump
    {
        internal static List<string> MainMenuTechLines;
        private static string mainMenuLabel = "run";

        private static string Dir()
        {
            string dir = Path.Combine(SpikeCsv.ResolveSpikeRoot(), SpikeS6.Id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static void WriteMainMenu(string label)
        {
            mainMenuLabel = label;
            try
            {
                var root = new ConfigNode("root");
                root.AddValue("label", label);
                root.AddValue("phase", "mainmenu");
                root.AddValue("utc", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

                // GameData folders
                var gd = root.AddNode("GAMEDATA");
                string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                var folders = new List<string>();
                foreach (string d in Directory.GetDirectories(gameData)) folders.Add(Path.GetFileName(d));
                folders.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in folders) gd.AddValue("folder", f);

                // Tech tree: the game database
                var dbLines = new List<string>();
                ConfigNode[] trees = GameDatabase.Instance.GetConfigNodes("TechTree");
                foreach (ConfigNode tree in trees)
                    foreach (ConfigNode rd in tree.GetNodes("RDNode"))
                    {
                        var parents = new List<string>();
                        foreach (ConfigNode p in rd.GetNodes("Parent")) parents.Add(p.GetValue("parentID"));
                        bool any = string.Equals(rd.GetValue("anyToUnlock"), "True", StringComparison.OrdinalIgnoreCase);
                        dbLines.Add(SpikeS6Content.TechLine(rd.GetValue("id"), rd.GetValue("cost"), any, parents));
                    }
                dbLines.Sort(StringComparer.Ordinal);
                MainMenuTechLines = dbLines;
                var tdb = root.AddNode("TECHTREE_DB");
                tdb.AddValue("trees", trees.Length);
                foreach (string l in dbLines) tdb.AddValue("line", l);

                // Tech tree: the stock file read raw, and the default TechTreeUrl
                string file = Path.Combine(gameData, Path.Combine("Squad", Path.Combine("Resources", "TechTree.cfg")));
                var tf = root.AddNode("TECHTREE_FILE");
                string url = "-";
                try { url = new GameParameters().Career.TechTreeUrl; } catch (Exception) { }
                tf.AddValue("defaultTechTreeUrl", url);
                tf.AddValue("exists", File.Exists(file));
                List<string> fileLines = File.Exists(file)
                    ? SpikeS6Content.TechLinesFromCfg(SpikeS6Cfg.Parse(File.ReadAllText(file)))
                    : new List<string>();
                foreach (string l in fileLines) tf.AddValue("line", l);
                var dbVsFile = SpikeS6Content.CompareTech(dbLines, fileLines, "db", "file");
                var cmp = root.AddNode("TECH_DB_VS_FILE");
                foreach (string p in dbVsFile) cmp.AddValue("problem", p);

                // Parts
                int parts = 0, noConfig = 0;
                var partsNode = root.AddNode("PARTS");
                foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                {
                    if (ap == null) continue;
                    parts++;
                    var pn = partsNode.AddNode("PART");
                    pn.AddValue("name", ap.name);
                    pn.AddValue("title", ap.title ?? "");
                    pn.AddValue("partUrl", ap.partUrl ?? "");
                    pn.AddValue("TechRequired", ap.TechRequired ?? "");
                    pn.AddValue("entryCost", SafeEntryCost(ap));
                    pn.AddValue("entryCostRaw", RawEntryCost(ap));
                    pn.AddValue("cost", ap.cost);
                    pn.AddValue("configFile", ap.configFileFullName ?? "");
                    if (ap.partConfig != null) ap.partConfig.CopyTo(pn.AddNode("CONFIG"));
                    else noConfig++;
                }

                int upgrades = CopyAll(root.AddNode("PARTUPGRADES"), "PARTUPGRADE");
                int experiments = CopyAll(root.AddNode("EXPERIMENTS"), "EXPERIMENT_DEFINITION");

                // Bodies
                int bodies = 0;
                var bn = root.AddNode("BODIES");
                if (FlightGlobals.Bodies != null)
                    foreach (CelestialBody b in FlightGlobals.Bodies)
                    {
                        if (b == null) continue;
                        bodies++;
                        var n = bn.AddNode("BODY");
                        n.AddValue("name", b.bodyName);
                        n.AddValue("radius", SpikeS6.Inv(b.Radius));
                        n.AddValue("isHomeWorld", b.isHomeWorld);
                        var sv = n.AddNode("scienceValues");
                        if (b.scienceValues != null)
                            foreach (FieldInfo fi in b.scienceValues.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                                sv.AddValue(fi.Name, SpikeS6Difficulty.FormatValue(fi.GetValue(b.scienceValues)));
                    }

                string path = Path.Combine(Dir(), "content-" + label + "-mainmenu.cfg");
                root.Save(path);
                SpikeS6.Info("content mainmenu label=" + label + " folders=" + SpikeS6.Inv(folders.Count)
                    + " dbTrees=" + SpikeS6.Inv(trees.Length) + " dbNodes=" + SpikeS6.Inv(dbLines.Count)
                    + " fileNodes=" + SpikeS6.Inv(fileLines.Count) + " dbVsFile=" + SpikeS6.Inv(dbVsFile.Count)
                    + " parts=" + SpikeS6.Inv(parts) + " partsNoConfig=" + SpikeS6.Inv(noConfig)
                    + " partUpgrades=" + SpikeS6.Inv(upgrades) + " experiments=" + SpikeS6.Inv(experiments)
                    + " bodies=" + SpikeS6.Inv(bodies) + " ctt=" + folders.Contains("CommunityTechTree")
                    + " pbc=" + folders.Contains("ProbesBeforeCrew") + " url=" + url + " file=" + path);
                SpikeS6.Csv("content.csv",
                    new[] { "label", "phase", "folders", "dbTrees", "dbNodes", "fileNodes", "dbVsFile", "parts", "partUpgrades", "experiments", "bodies" },
                    label, "mainmenu", folders.Count, trees.Length, dbLines.Count, fileLines.Count, dbVsFile.Count,
                    parts, upgrades, experiments, bodies);
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("content mainmenu failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int CopyAll(ConfigNode into, string nodeName)
        {
            int n = 0;
            foreach (ConfigNode c in GameDatabase.Instance.GetConfigNodes(nodeName))
            {
                c.CopyTo(into.AddNode(nodeName));
                n++;
            }
            return n;
        }

        private static string SafeEntryCost(AvailablePart ap)
        {
            try { return SpikeS6.Inv(ap.entryCost); }
            catch (Exception ex) { return "<error:" + ex.GetType().Name + ">"; }
        }

        private static string RawEntryCost(AvailablePart ap)
        {
            FieldInfo f = typeof(AvailablePart).GetField("_entryCost", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? SpikeS6Difficulty.FormatValue(f.GetValue(ap)) : "<missing>";
        }

        /// <summary>The R&amp;D building's tree, as canonical lines, compared with the main-menu read.</summary>
        internal static void ReadRnD(string source)
        {
            try
            {
                var tree = AssetBase.RnDTechTree;
                ProtoRDNode[] nodes = tree != null ? tree.GetTreeNodes() : null;
                var lines = new List<string>();
                if (nodes != null)
                    foreach (ProtoRDNode n in nodes)
                    {
                        if (n == null || n.tech == null) continue;
                        var parents = new List<string>();
                        if (n.parents != null)
                            foreach (ProtoRDNode p in n.parents)
                                if (p != null && p.tech != null) parents.Add(p.tech.techID);
                        lines.Add(SpikeS6Content.TechLine(n.tech.techID, SpikeS6.Inv(n.tech.scienceCost), n.AnyParentToUnlock, parents));
                    }
                lines.Sort(StringComparer.Ordinal);

                var root = new ConfigNode("root");
                root.AddValue("label", mainMenuLabel);
                root.AddValue("phase", "rnd-" + source);
                var t = root.AddNode("TECHTREE_RND");
                foreach (string l in lines) t.AddValue("line", l);
                List<string> problems = MainMenuTechLines != null
                    ? SpikeS6Content.CompareTech(MainMenuTechLines, lines, "mainmenu", "rnd")
                    : null;
                var c = root.AddNode("TECH_MAINMENU_VS_RND");
                if (problems != null) foreach (string p in problems) c.AddValue("problem", p);
                string path = Path.Combine(Dir(), "content-" + mainMenuLabel + "-rnd-" + source + ".cfg");
                root.Save(path);

                string result = problems == null ? "NO-MAINMENU-READ" : problems.Count == 0 ? "PASS" : "FAIL";
                SpikeS6.Info("rnd-tree source=" + source + " tree=" + (tree != null) + " nodes=" + SpikeS6.Inv(lines.Count)
                    + " vsMainMenu=" + (problems == null ? "-" : SpikeS6.Inv(problems.Count)) + " result=" + result
                    + (problems != null && problems.Count > 0 ? " first=" + problems[0] : "") + " file=" + path);
                SpikeS6.Csv("content.csv",
                    new[] { "label", "phase", "folders", "dbTrees", "dbNodes", "fileNodes", "dbVsFile", "parts", "partUpgrades", "experiments", "bodies" },
                    mainMenuLabel, "rnd-" + source, "", "", lines.Count, "", problems == null ? -1 : problems.Count, "", "", "", "");
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("rnd-tree read failed source=" + source + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
